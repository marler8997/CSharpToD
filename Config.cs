using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CSharpToD
{
    delegate void DirectiveBlockParser(Config config, UInt32 lineNumber, String line);
    public class ProjectConfig
    {
        public readonly string projectFile;
        public OutputType? outputType;
        public List<string> typesToRemove = new List<string>();
        public ProjectConfig(string projectFile)
        {
            this.projectFile = projectFile;
            this.outputType = OutputType.Library;
        }
        public void ProcessBlockLine(Config config, UInt32 lineNumber, String line)
        {
            if (line.StartsWith("OutputType "))
            {
                String outputTypeString = config.ProcessString(lineNumber,
                    config.OneArg("OutputType", lineNumber, line, (uint)"OutputType ".Length, "output type"));
                this.outputType = (OutputType)Enum.Parse(typeof(OutputType), outputTypeString);
            }
            else if(line.StartsWith("Remove "))
            {
                typesToRemove.Add(config.ProcessString(lineNumber,
                    config.OneArg("Remove", lineNumber, line, (uint)"Remove ".Length, "type")));
            }
            else
            {
                throw new ErrorMessageException(String.Format("{0}({1}): unknown directive inside project block: \"{2}\"",
                    config.filename, lineNumber, line));
            }
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
        public readonly Boolean noMscorlib;
        public readonly List<string> includePaths = new List<string>();
        public readonly List<string> libraries = new List<string>();
        public readonly List<IncludeSource> includeSources = new List<IncludeSource>();
        public readonly List<ProjectConfig> projects = new List<ProjectConfig>();
        public readonly Dictionary<string, string> vars = new Dictionary<string, string>();
        public readonly Dictionary<string, string> msbuildProperties = new Dictionary<string, string>();
        public readonly Dictionary<string, string> assemblyPackageOverrides = new Dictionary<string, string>();
        public readonly List<string> sourceDefines = new List<string>();

        public Config(String filename)
        {
            this.filename = filename;
            DirectiveBlockParser nextBlockParser = null;
            using (StreamReader reader = new StreamReader(new FileStream(
                filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
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

                    // Handle a block
                    if(line.Length == 1 && line[0] == '{')
                    {
                        if(nextBlockParser == null)
                        {
                            throw new ErrorMessageException(String.Format(
                                "{0}({1}): '{{' block '}}' ProjectConfig configappeared underneath an invalid directive",
                                filename, lineNumber));
                        }
                        while(true)
                        {
                            line = reader.ReadLine();
                            if(line == null)
                            {
                                throw new ErrorMessageException(String.Format(
                                    "{0}({1}): config ended inside a '{{' block '}}'",
                                    filename, lineNumber));
                            }
                            lineNumber++;
                            line = line.Trim();
                            if (line.Length == 0 || line[0] == '#')
                            {
                                continue;
                            }
                            if(line.Length == 1 && line[0] == '}')
                            {
                                break;
                            }
                            nextBlockParser(this, lineNumber, line);
                        }
                        nextBlockParser = null;
                        continue;
                    }

                    nextBlockParser = null; // Important
                    if(line.StartsWith("Project "))
                    {
                        String projectFile = OneArg("Project", lineNumber, line, (uint)"Project ".Length, "project");
                        projectFile = ProcessString(lineNumber, projectFile);
                        projectFile = projectFile.Replace('/', Path.DirectorySeparatorChar);
                        var projectConfig = new ProjectConfig(projectFile);
                        nextBlockParser = projectConfig.ProcessBlockLine;
                        projects.Add(projectConfig);
                    }
                    else if(line.StartsWith("NoMscorlib"))
                    {
                        this.noMscorlib = true;
                    }
                    else if(line.StartsWith("IncludePath "))
                    {
                        includePaths.Add(ProcessString(lineNumber,
                            OneArg("IncludePath", lineNumber, line, (uint)"IncludePath ".Length, "include path")));
                    }
                    else if (line.StartsWith("Library "))
                    {
                        libraries.Add(ProcessString(lineNumber,
                            OneArg("Library", lineNumber, line, (uint)"Library ".Length, "library")));
                    }
                    else if(line.StartsWith("IncludeSource "))
                    {
                        String @namespace;
                        String file = TwoArgs(out @namespace, "IncludeSource", lineNumber, line,
                            (uint)"IncludeSource ".Length, "namespace", "file");
                        @namespace = ProcessString(lineNumber, @namespace);
                        file = ProcessString(lineNumber, file);
                        file = file.Replace('/', Path.DirectorySeparatorChar);
                        includeSources.Add(new IncludeSource(@namespace, file));
                    }
                    else if(line.StartsWith("Set "))
                    {
                        String varName;
                        String value = TwoArgs(out varName, "Set", lineNumber, line,
                            (uint)"Set ".Length, "name", "value");
                        varName = ProcessString(lineNumber, varName);
                        value = ProcessString(lineNumber, value);
                        vars.Add(varName, value);
                        //Console.WriteLine("\"{0}\" = \"{1}\"", varName, value);
                    }
                    else if (line.StartsWith("SetMSBuild "))
                    {
                        String varName;
                        String value = TwoArgs(out varName, "SetMSBuild", lineNumber, line,
                            (uint)"SetMSBuild ".Length, "name", "value");
                        varName = ProcessString(lineNumber, varName);
                        value = ProcessString(lineNumber, value);
                        msbuildProperties.Add(varName, value);
                        //Console.WriteLine("\"{0}\" = \"{1}\"", varName, value);
                    }
                    else if(line.StartsWith("AssemblyPackageOverride "))
                    {
                        String assemblyName;
                        String packageName = TwoArgs(out assemblyName, "AssemblyPackageOverride", lineNumber, line,
                            (uint)"AssemblyPackageOverride ".Length, "assembly", "package");
                        assemblyName = ProcessString(lineNumber, assemblyName);
                        packageName = ProcessString(lineNumber, packageName);
                        assemblyPackageOverrides.Add(assemblyName, packageName);
                    }
                    else if(line.StartsWith("SourceDefine "))
                    {
                        sourceDefines.Add(ProcessString(lineNumber,
                            OneArg("SourceDefine", lineNumber, line, (uint)"SourceDefine ".Length, "source define")));
                    }
                    else
                    {
                        throw new ErrorMessageException(String.Format("{0}({1}): unknown directive: \"{2}\"",
                            filename, lineNumber, line));
                    }
                }
            }
        }

        public String OneArg(String directive, UInt32 lineNumber, String line, UInt32 offset, String argName)
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
        public String TwoArgs(out String outArg1, String directive, UInt32 lineNumber, String line, UInt32 offset, String arg1Name, String arg2Name)
        {
            String rest;
            outArg1 = line.Peel((int)offset, out rest);
            if (String.IsNullOrEmpty(outArg1))
            {
                throw new ErrorMessageException(String.Format("{0}({1}): The {2} directive must have a(n) {3}",
                    filename, lineNumber, directive, arg1Name));
            }
            String arg2 = rest.Peel(0, out rest);
            if (rest != null && rest.Trim().Length != 0)
            {
                throw new ErrorMessageException(String.Format("{0}({1}): The {2} directive line has too many arguments",
                    filename, lineNumber, directive));
            }
            return arg2;
        }

        public String ProcessString(UInt32 lineNumber, String str)
        {
            int indexOfFirstDollar = str.IndexOf('$');
            if (indexOfFirstDollar < 0)
            {
                return str;
            }

            StringBuilder builder = new StringBuilder(str.Length * 2);
            builder.Append(str, 0, indexOfFirstDollar);

            for(int i = indexOfFirstDollar; ;)
            {
                i++;
                if(i >= str.Length)
                {
                    throw new FormatException(String.Format("{0}({1}): String cannot end with '$'",
                        filename, lineNumber));
                }
                if(str[i] != '(')
                {
                    throw new FormatException(String.Format("{0}({1}): The '$' character must be followed by '(', but got '{2}'",
                        filename, lineNumber, str[i]));
                }
                i++;
                int varStartIndex = i;
                for(;;i++)
                {
                    if(i >= str.Length)
                    {
                        throw new FormatException(String.Format("{0}({1}): The '$(' sequence must have and ending ')'",
                            filename, lineNumber));
                    }
                    if(str[i] == ')')
                    {
                        break;
                    }
                }
                String varName = str.Substring((int)varStartIndex, (int)(i - varStartIndex));
                String value;
                if(!vars.TryGetValue(varName, out value))
                {
                    throw new FormatException(String.Format("{0}({1}): The $({2}) variable has not been set",
                        filename, lineNumber, varName));
                }
                builder.Append(value);

                // Find the next variable
                i++;
                int saveOffset = i;
                for(;;i++)
                {
                    if(i >= str.Length)
                    {
                        builder.Append(str, saveOffset, i - saveOffset);
                        return builder.ToString();
                    }
                    if(str[i] == '$')
                    {
                        builder.Append(str, saveOffset, i - saveOffset);
                        break;
                    }
                }
            }
        }
    }
}
