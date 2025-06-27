﻿/**
 * Copyright (c) Crista Lopes (aka Diva). All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without modification, 
 * are permitted provided that the following conditions are met:
 * 
 *     * Redistributions of source code must retain the above copyright notice, 
 *       this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright notice, 
 *       this list of conditions and the following disclaimer in the documentation 
 *       and/or other materials provided with the distribution.
 *     * Neither the name of the Organizations nor the names of Individual
 *       Contributors may be used to endorse or promote products derived from 
 *       this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND 
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES 
 * OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL 
 * THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, 
 * EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE 
 * GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED 
 * AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED 
 * OF THE POSSIBILITY OF SUCH DAMAGE.
 * 
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using Diva.Interfaces;
using Diva.Utils;

using log4net;

namespace Diva.Wifi.WifiScript
{
    public class Processor
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // <!-- #directive [args] -->
        private static Regex ssi = new Regex("<!--\\s*\\#(\\S+)\\s+(\\S*)\\s*-->");
        // name="value"
        private static Regex args = new Regex("(\\w+)\\s*=\\s*(\\S+)");

        private IWifiScriptFace m_WebApp;
        private Type m_WebAppType;
        private Type m_ExtensionMethods;

        private IEnvironment m_Env;
        private List<object> m_ListOfObjects;
        private int m_Index;
        private static string m_FileName;
        private int recursions;

        public Processor(IWifiScriptFace webApp, IEnvironment env)
            : this(webApp, null, env, null)
        {
        }

        public Processor(IWifiScriptFace webApp, Type extMeths, IEnvironment env, List<object> lot)
        {
            m_WebApp = webApp;
            m_WebAppType = m_WebApp.GetType();
            m_ExtensionMethods = extMeths;
            m_Env = env;
            m_ListOfObjects = lot;
            m_Index = 0;
            //m_log.DebugFormat("[Wifi]: New processor m_Index = {0}", m_Index);
        }

        public string Process(string html)
        {
            var output = new StringBuilder();
            var stack = new Stack<(string content, int index, string fileName, List<object> dataList)>();
            stack.Push((html, m_Index, m_FileName, m_ListOfObjects));

            while (stack.Count > 0)
            {
                var (currentHtml, currentIndex, currentFileName, currentDataList) = stack.Pop();
                m_Index = currentIndex;
                m_FileName = currentFileName;
                m_ListOfObjects = currentDataList;

                int lastIndex = 0;
                var matches = ssi.Matches(currentHtml);

                if (matches.Count == 0)
                {
                    output.Append(currentHtml);
                    continue;
                }

                for (int i = 0; i < matches.Count; i++)
                {
                    var match = matches[i];
                    output.Append(currentHtml.Substring(lastIndex, match.Index - lastIndex));
                    lastIndex = match.Index + match.Length;

                    string directive = match.Groups[1].Value;
                    string argStr = match.Groups[2].Value;

                    if (directive == "include")
                    {
                        var includeMatch = args.Match(argStr);
                        if (includeMatch.Groups.Count == 3)
                        {
                            string path = m_WebApp.LocalizePath(m_Env, includeMatch.Groups[2].Value);

                            if (File.Exists(path))
                            {
                                string includedContent = File.ReadAllText(path);

                                if (path != m_FileName)
                                {
                                    var newList = currentDataList;
                                    int newIndex = currentIndex;

                                    if (newList != null && newIndex < newList.Count - 1)
                                    {
                                        newIndex++;
                                        if (newList[newIndex] is List<object> nestedList)
                                            newList = nestedList;
                                    }

                                    stack.Push((currentHtml.Substring(lastIndex), currentIndex, currentFileName, currentDataList));
                                    stack.Push((includedContent, newIndex, path, newList));
                                    break;
                                }
                                else
                                {
                                    m_Index++;
                                    if (currentDataList != null && m_Index >= currentDataList.Count)
                                        continue;

                                    stack.Push((currentHtml.Substring(lastIndex), currentIndex, currentFileName, currentDataList));
                                    stack.Push((includedContent, m_Index, path, currentDataList));
                                    break;
                                }
                            }
                        }
                    }
                    else if (directive == "get")
                    {
                        output.Append(Get(argStr));
                    }
                    else if (directive == "call")
                    {
                        output.Append(Call(argStr));
                    }
                }

                if (lastIndex < currentHtml.Length)
                {
                    output.Append(currentHtml.Substring(lastIndex));
                }
            }

            return output.ToString();
        }

        private string Process(Match match)
        {
            string directive = string.Empty;
            string argStr = string.Empty;
            //m_log.DebugFormat("Groups: {0}", match.Groups.Count);
            //foreach (Group g in match.Groups)
            //{
            //    m_log.DebugFormat(" --> {0} {1}", g.Value, g.Success);
            //}
            // The first group is always the overall match
            if (match.Groups.Count > 1)
                directive = match.Groups[1].Value;
            if (match.Groups.Count > 2)
                argStr = match.Groups[2].Value;

            if (directive != string.Empty)
            {
                return Eval(directive, argStr);
            }

            return string.Empty;
        }

        private string Eval(string directive, string argStr)
        {
            //m_log.DebugFormat("[WifiScript]: Interpret {0} {1}", directive, argStr);

            if (directive.Equals("include"))
                return Include(argStr);

            if (directive.Equals("get"))
                return Get(argStr);

            if (directive.Equals("call"))
                return Call(argStr);

            return string.Empty;
        }

        private string Include(string argStr)
        {

            Match match = args.Match(argStr);
            //m_log.DebugFormat("Match {0} args? {1} {2}", args.ToString(), match.Success, match.Groups.Count);
            if (match.Groups.Count == 3)
            {
                string name = match.Groups[1].Value;
                string value = match.Groups[2].Value;
                // ignore the name which should be file
                string file = m_WebApp.LocalizePath(m_Env, value);
                //m_log.DebugFormat("[WifiScript]: Including file {0} with index = {1} (previous file is {2})", file, m_Index, m_FileName);

                string content = string.Empty;
                using (StreamReader sr = new StreamReader(file))
                {
                    if (file == m_FileName)
                    {
                        m_Index++;
                        if (m_ListOfObjects != null)
                        {
                            if (m_Index >= m_ListOfObjects.Count)
                            {
                                return string.Empty;
                            }
                        }

                        content = sr.ReadToEnd();
                    }
                    else
                    {
                        string oldFile = m_FileName;
                        m_FileName = file;
                        List<object> nextLoo = m_ListOfObjects;
                        if (m_ListOfObjects != null && m_Index < m_ListOfObjects.Count - 1)
                        {
                            m_Index++;
                            if (m_ListOfObjects[m_Index] is List<object>)
                            //if (IsGenericList(m_ListOfObjects[m_Index].GetType()))
                                nextLoo = (List<object>)m_ListOfObjects[m_Index];
                        }
                        Processor p = new Processor(m_WebApp, m_ExtensionMethods, m_Env, nextLoo);
                        //Processor p = new Processor(m_WebApp, m_ExtensionMethods, m_Env, m_ListOfObjects);
                        string result = p.Process(sr.ReadToEnd());
                        m_FileName = oldFile;
                        return result;
                    }
                }

                // recurse!
                // TODO: This is bad design and only a bandaid to prevent crashes, avoid recursions!
                // Page handling needs work to prevent this from occurring in the first place
                if (!string.IsNullOrEmpty(content))
                {
                    recursions++;
                    if (recursions > 10)
                        return string.Empty;

                    return Process(content);
                }
            }

            return string.Empty;
        }

        private string Get(string argStr)
        {
            Match match = args.Match(argStr);
            //m_log.DebugFormat("[WifiScript]: Get macthed {0} groups", match.Groups.Count);
            if (match.Groups.Count == 3)
            {
                string kind = match.Groups[1].Value;
                string name = match.Groups[2].Value;
                string keyname = string.Empty;

                object value = null;
                bool translate = false;

                if (kind == "var")
                {
                    // First, try the WebApp 
                    PropertyInfo pinfo = m_WebAppType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty | BindingFlags.DeclaredOnly);
                    if (pinfo != null)
                        value = pinfo.GetValue(m_WebApp, null);
                    else
                    {
                        //m_log.DebugFormat("[WifiScript]: Variable {0} not found in {1}. Trying Data type.", name, pinfo.ReflectedType);
                        // Try the Data type
                        if (m_ListOfObjects != null && m_ListOfObjects.Count > 0 && m_Index < m_ListOfObjects.Count)
                        {
                            object o = m_ListOfObjects[GetIndex()];
                            Type type = o.GetType();

                            try
                            {
                                pinfo = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty | BindingFlags.DeclaredOnly);
                                value = pinfo.GetValue(o, null);
                                //m_log.DebugFormat("[WifiScript] Replaced {0} with {1}", name, value.ToString());
                            }
                            catch (Exception e)
                            {
                                m_log.DebugFormat("[WifiScript]: Exception in GetProperty {0}", e.Message);
                            }
                        }

                    }
                    if (pinfo != null)
                        translate = Attribute.IsDefined(pinfo, typeof(TranslateAttribute));
                }
                /*
                //when a 'get method' is performed, the named method is invoked
                //on list of objects and the string representation of output is returned
                // [Obsolete] This should be removed when the other options are proven to work
                else if (kind == "method")
                {
                    if (m_ListOfObjects != null && m_ListOfObjects.Count > 0 && m_Index < m_ListOfObjects.Count)
                    {
                        object o = m_ListOfObjects[GetIndex()];
                        Type type = o.GetType();

                        try
                        {
                            MethodInfo met = type.GetMethod(name);
                            value = (string)met.Invoke(o, null).ToString();
                        }
                        catch (Exception e)
                        {
                            m_log.DebugFormat("[WifiScript]: Exception in invoke {0}", e.Message);
                        }
                    }
                }
                */
                else if (kind == "field")
                {
                    if (m_ListOfObjects != null && m_ListOfObjects.Count > 0 && m_Index < m_ListOfObjects.Count)
                    {
                        // Let's search in the list of objects
                        object o = m_ListOfObjects[GetIndex()];
                        Type type = o.GetType();
                        FieldInfo finfo = type.GetField(name, BindingFlags.Instance | BindingFlags.Public );
                        if (finfo != null)
                        {
                            value = finfo.GetValue(o);
                            translate = Attribute.IsDefined(finfo, typeof(TranslateAttribute));
                        }
                        else // Try properties
                        {
                            PropertyInfo pinfo = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public );
                            if (pinfo != null)
                            {
                                value = pinfo.GetValue(o, null);
                                translate = Attribute.IsDefined(pinfo, typeof(TranslateAttribute));
                            }
                            else
                                m_log.DebugFormat("[WifiScript]: Field {0} not found in type {1}; {2}", name, type, argStr);
                        }
                    }
                }

                if (value != null)
                {
                    if (translate)
                        return m_WebApp.Translate(m_Env, value.ToString());
                    else
                        return value.ToString();
                }
            }

            return string.Empty;
        }

        private string Call(string argStr)
        {
            MatchCollection matches = args.Matches(argStr);
            List<String> arguments = new List<string>();
            String methodName = string.Empty;
            foreach (Match match in matches)
            {
                if (match.Groups.Count == 3)
                {
                    string name = match.Groups[1].Value;
                    string value = match.Groups[2].Value;
                    if (name.Equals("method"))
                        methodName = value;
                    else
                        arguments.Add(value);
                }
            }
            if (!methodName.Equals(string.Empty))
            {
                object[] arg = new object[] { m_Env };
                // First try the WebApp
                try
                {
                    if (m_WebAppType.GetMethod(methodName) != null)
                    {
                        String s = (String)m_WebAppType.InvokeMember(methodName,
                            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                            null, m_WebApp, arg);

                        return s;
                    }
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[WifiScript]: Exception in invoke {0} in WebApp {1}", methodName, e.Message);
                    if (e.InnerException != null)
                        m_log.DebugFormat("[WifiScript]: Inner Exception {0}", e.InnerException.Message);
                }

                // Then try the Data type
                try
                {
                    if (m_ListOfObjects != null && m_ListOfObjects.Count > 0 && m_Index < m_ListOfObjects.Count)
                    {
                        object o = m_ListOfObjects[GetIndex()];
                        if (o != null)
                        {
                            Type type = o.GetType();
                            if (type != null)
                            {
                                MethodInfo met = type.GetMethod(methodName);

                                if (met != null)
                                {
                                    String s = (String)met.Invoke(o, null);
                                    return s;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[WifiScript]: Exception in invoke {0} in data type {1}", methodName, e.Message);
                    if (e.InnerException != null)
                        m_log.DebugFormat("[WifiScript]: Inner Exception {0}", e.InnerException.Message);
                }
                // Then try the Extension Methods
                try
                {
                    //m_log.DebugFormat(" --> call method {0}; count {1}", methodName,  (m_ListOfObjects == null ? "null" : m_ListOfObjects.Count.ToString()));
                    if (m_ListOfObjects != null && m_ListOfObjects.Count > 0 && m_Index < m_ListOfObjects.Count)
                    {
                        object o = m_ListOfObjects[GetIndex()];
                        if (m_ExtensionMethods.GetMethod(methodName) != null)
                        {
                            arg = new object[] { o, m_Env };
                            //m_log.DebugFormat(" --> {0}", o.ToString());
                            string value = (string)m_ExtensionMethods.InvokeMember(methodName,
                                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static,
                                null, null, arg);

                            return value;
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[WifiScript]: Exception in invoke extension method {0}, {1}", methodName, e.Message);
                    if (e.InnerException != null)
                        m_log.DebugFormat("[WifiScript]: Inner Exception {0}", e.InnerException.Message);
                }
            }

            return string.Empty;
        }

        private int GetIndex()
        {
            return (m_Index == -1) ? 0 : m_Index;
        }

        public bool IsGenericList(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }
            foreach (Type @interface in type.GetInterfaces())
            {
                if (@interface.IsGenericType)
                {
                    if (@interface.GetGenericTypeDefinition() == typeof(ICollection<>))
                    {
                        // if needed, you can also return the type used as generic argument
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
