using System;
using System.Collections.Generic;
using System.IO;

namespace CSharpToD
{
    public struct ProjectConfig
    {
        public readonly string projectFile;
        public ProjectConfig(string projectFile)
        {
            this.projectFile = projectFile;
        }
    }
    public struct IncludeSource
    {
        public readonly string @namespace;
        public readonly string filename;
        public IncludeSource(string @namespace, string filename)
        {
            this.@namespace = @namespace;
            this.filename = filename;
        }
    }
    public enum OutputType
    {
        Library,
        Exe,
    }
    public class Config
    {
        public readonly String filename;
        public readonly OutputType outputType;
        public readonly String outputName;
        public readonly Boolean noMscorlib;
        public readonly List<string> includePaths = new List<string>();
        public readonly List<string> libraries = new List<string>();
        public readonly List<IncludeSource> includeSources = new List<IncludeSource>();
        public readonly List<ProjectConfig> projects = new List<ProjectConfig>();

        public Config(String filename)
        {
            this.filename = filename;
            using (StreamReader reader = new StreamReader(new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                uint lineNumber = 0;
                while(true)
                {
                    String line = reader.ReadLine();
                    if(line == null)
                    {
                        break;
                    }
                    lineNumber++;
                    line = line.Trim();
                    if(line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    if(line.StartsWith("Project "))
                    {
                        String projectFile = OneArg("Project", lineNumber, line, (uint)"Project ".Length, "project");
                        projectFile = projectFile.Replace('/', Path.DirectorySeparatorChar);
                        projects.Add(new ProjectConfig(projectFile));
                    }
                    else if(line.StartsWith("NoMscorlib"))
                    {
                        this.noMscorlib = true;
                    }
                    else if(line.StartsWith("OutputType "))
                    {
                        String outputTypeString = OneArg("OutputType", lineNumber, line, (uint)"OutputType ".Length, "output type");
                        this.outputType = (OutputType)Enum.Parse(typeof(OutputType), outputTypeString);
                    }
                    else if(line.StartsWith("OutputName "))
                    {
                        this.outputName = OneArg("OutputType", lineNumber, line, (uint)"OutputType ".Length, "output type");
                    }
                    else if(line.StartsWith("IncludePath "))
                    {
                        includePaths.Add(OneArg("IncludePath", lineNumber, line, (uint)"IncludePath ".Length, "include path"));
                    }
                    else if (line.StartsWith("Library "))
                    {
                        libraries.Add(OneArg("Library", lineNumber, line, (uint)"Library ".Length, "library"));
                    }
                    else if(line.StartsWith("IncludeSource "))
                    {
                        String rest;
                        String @namespace = line.Peel(14, out rest);
                        if (String.IsNullOrEmpty(@namespace))
                        {
                            throw new ErrorMessageException(String.Format("{0}({1}): IncludeSource line must have a namespace and file",
                                filename, lineNumber));
                        }
                        String file = rest.Peel(0, out rest);
                        if (rest != null && rest.Trim().Length != 0)
                        {
                            throw new ErrorMessageException(String.Format("{0}({1}): IncludeSource line has too many arguments",
                                filename, lineNumber));
                        }
                        file = file.Replace('/', Path.DirectorySeparatorChar);
                        includeSources.Add(new IncludeSource(@namespace, file));
                    }
                    else
                    {
                        throw new ErrorMessageException(String.Format("{0}({1}): unknown directive: {2}",
                            filename, lineNumber, line));
                    }
                }
            }
        }

        String OneArg(String directive, UInt32 lineNumber, String line, UInt32 offset, String argName)
        {
            String rest;
            String argString = line.Peel((int)offset, out rest);
            if (String.IsNullOrEmpty(argString))
            {
                throw new ErrorMessageException(String.Format("{0}({1}): The {2} directive must have a(n) {3}",
                    filename, lineNumber, directive, argName));
            }
            if (rest != null && rest.Trim().Length != 0)
            {
                throw new ErrorMessageException(String.Format("{0}({1}): The {2} directive has too many arguments",
                    filename, lineNumber, directive));
            }
            return argString;
        }
    }

    public static class StringExtensions
    {
        /// <summary>
        /// Peel the next non-whitespace substring from the front of the given string.
        /// </summary>
        /// <param name="str">The string to peel from</param>
        /// <param name="rest">The rest of the string after the peel</param>
        /// <returns>The peeled string</returns>
        public static String Peel(this String str, out String rest)
        {
            return Peel(str, 0, out rest);
        }

        /// <summary>
        /// Peel the next non-whitespace substring from the given offset of the given string.
        /// </summary>
        /// <param name="str">The string to peel from</param>
        /// <param name="offset">The offset into the string to start peeling from.</param>
        /// <param name="rest">The rest of the string after the peel</param>
        /// <returns>The peeled string</returns>
        public static String Peel(this String str, Int32 offset, out String rest)
        {
            if (str == null)
            {
                rest = null;
                return null;
            }

            Char c;

            //
            // Skip beginning whitespace
            //
            while (true)
            {
                if (offset >= str.Length)
                {
                    rest = null;
                    return null;
                }
                c = str[offset];
                if (!Char.IsWhiteSpace(c)) break;
                offset++;
            }

            Int32 startOffset = offset;

            //
            // Find next whitespace
            //
            while (true)
            {
                offset++;
                if (offset >= str.Length)
                {
                    rest = null;
                    return str.Substring(startOffset);
                }
                c = str[offset];
                if (Char.IsWhiteSpace(c)) break;
            }

            Int32 peelLimit = offset;

            //
            // Remove whitespace till rest
            //
            while (true)
            {
                offset++;
                if (offset >= str.Length)
                {
                    rest = null;
                }
                if (!Char.IsWhiteSpace(str[offset]))
                {
                    rest = str.Substring(offset);
                    break;
                }
            }
            return str.Substring(startOffset, peelLimit - startOffset);
        }
    }
    }
