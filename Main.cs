using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToD
{
    class AlreadyPrintedErrorException : Exception
    {
        public AlreadyPrintedErrorException()
            : base(null)
        {

        }
    }
    class ErrorMessageException : Exception
    {
        public ErrorMessageException(String message)
            : base(message)
        {
        }
    }
    class CSharpToD
    {
        static Boolean clean;
        public static Boolean noWarnings;
        public static Boolean ignoreFileErrors;
        public static Boolean printSourceFiles;
        public static Boolean skeleton;
        public static Boolean generateDebug;

        public static String conversionRoot;
        public static String generatedCodePath;
        public static String mscorlibPath;
        public static Config config;

        static void Usage()
        {
            Console.WriteLine("csharptod [cs2d.config]");
            Console.WriteLine("Options:");
            Console.WriteLine("  -g                     Generate code with debugging around it");
            Console.WriteLine("  --clean                Clean the generated first");
            Console.WriteLine("  --no-warning           Do not print the warnings");
            Console.WriteLine("  --ignore-filre-errors  Ignore syntax/semantic errors");
            Console.WriteLine("  --print-source-files   Print source files when building");
            Console.WriteLine("  --skeleton             Generate types without code");
        }
        static String AssertArg(string[] args, ref int index)
        {
            index++;
            if(index >= args.Length)
            {
                throw new ErrorMessageException(String.Format("option '{0}' requires an argument", args[index - 1]));
            }
            return args[index];
        }
        static int getopt(string[] args)
        {
            int saveIndex = 0;
            for (int i = 0; i < args.Length; i++)
            {
                String arg = args[i];
                if (arg.StartsWith("--"))
                {
                    if(arg.Equals("--clean"))
                    {
                        clean = true;
                    }
                    else if(arg.Equals("--no-warnings"))
                    {
                        noWarnings = true;
                    }
                    else if(arg.Equals("--ignore-file-errors"))
                    {
                        ignoreFileErrors = true;
                    }
                    else if(arg.Equals("--print-source-files"))
                    {
                        printSourceFiles = true;
                    }
                    else if (arg.Equals("--skeleton"))
                    {
                        skeleton = true;
                    }
                    else
                    {
                        throw new ErrorMessageException(String.Format("Unknown option: {0}", arg));
                    }
                }
                else if (arg.StartsWith("-"))
                {
                    if (arg.Equals("-g"))
                    {
                        generateDebug = true;
                    }
                    else
                    {
                        throw new ErrorMessageException(String.Format("Unknown option: {0}", arg));
                    }
                }
                else
                {
                    args[saveIndex++] = arg;
                }
            }

            return saveIndex;
        }
        static int Main(string[] args)
        {
            var startTime = Stopwatch.GetTimestamp();
            try
            {
                int argsLength = getopt(args);

                String configFile = null;
                if (argsLength == 1)
                {
                    configFile = args[0];
                }
                else if(argsLength > 1)
                {
                    Console.WriteLine("Error: too many command line arguments");
                    return 1;
                }

                if(configFile == null)
                {
                    configFile = FindConversionRootAndConfigFile();
                }
                else
                {
                    if(!File.Exists(configFile))
                    {
                        throw new ErrorMessageException(String.Format("{0} does not exist", configFile));
                    }
                    conversionRoot = Path.GetDirectoryName(configFile);
                }
                config = new Config(configFile);
                if(config.projects.Count == 0)
                {
                    Console.WriteLine("There are no projects configured");
                    return 0;
                }
                mscorlibPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    Path.Combine("..",
                    Path.Combine("..",
                    Path.Combine("mscorlib", "cs2d")))));

                if (!config.noMscorlib)
                {
                    String mscorlibFilename = Path.Combine(mscorlibPath, "mscorlib.lib");
                    // Check that mscorlib exists
                    if(!File.Exists(mscorlibFilename))
                    {
                        Console.WriteLine("Error: mscorlib is not built: {0}", mscorlibFilename);
                        return 1;
                    }
                }

                //Console.WriteLine("[DEBUG] There are {0} project(s)", config.projects.Count);
                ProjectModels[] projectModelsArray = new ProjectModels[config.projects.Count];
                {
                    int projectIndex = 0;
                    foreach (ProjectConfig projectConfig in config.projects)
                    {
                        String projectFileFullPath = Path.Combine(conversionRoot, projectConfig.projectFile);
                        if (!File.Exists(projectFileFullPath))
                        {
                            Console.WriteLine("Error: project does not exist: {0}", projectFileFullPath);
                            return 1;
                        }
                        projectModelsArray[projectIndex] = new ProjectModels(projectConfig, projectFileFullPath);
                        projectIndex++;
                    }
                }

                // Setup Directory
                generatedCodePath = Path.Combine(conversionRoot, "cs2d");
                if (Directory.Exists(generatedCodePath))
                {
                    if (clean)
                    {
                        Console.WriteLine("[{0}] Cleaning '{1}'", Thread.CurrentThread.ManagedThreadId, generatedCodePath);
                        Directory.Delete(generatedCodePath, true);
                        Directory.CreateDirectory(generatedCodePath);
                    }
                }
                else
                {
                    Directory.CreateDirectory(generatedCodePath);
                }

                MSBuildWorkspace workspace = MSBuildWorkspace.Create(config.msbuildProperties);
                workspace.LoadMetadataForReferencedProjects = true;
                workspace.SkipUnrecognizedProjects = false;

                // Start the tasks to load and process all the projects/files
                for (int projectIndex = 0; projectIndex < projectModelsArray.Length; projectIndex++)
                {
                    ProjectModels projectModels = projectModelsArray[projectIndex];
                    Console.WriteLine("[{0}] Starting project loader for '{1}'...",
                        Thread.CurrentThread.ManagedThreadId, projectModels.fileFullPath);
                    TaskManager.AddTask(workspace.OpenProjectAsync(projectModels.fileFullPath).
                        ContinueWith(projectModels.ProjectLoaded));
                }

                // Wait for all files in all projects to be processed
                if(!TaskManager.WaitLoop())
                {
                    return 1; // fail
                }

                DlangBuildGenerator.MakeDProjectFile(config, projectModelsArray);

                //
                // Compile D Code
                //
                String rdmdProgram = FindProgram("rdmd");
                if (rdmdProgram == null)
                {
                    Console.WriteLine("rdmd not found, cannot compile D code");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("======================= Compiling D Code ========================");
                    {
                        Process compilerProcess = new Process();
                        compilerProcess.StartInfo.FileName = rdmdProgram;
                        compilerProcess.StartInfo.Arguments = Path.Combine(generatedCodePath, "build.d");
                        compilerProcess.StartInfo.UseShellExecute = false;
                        Console.WriteLine("[RUN] '{0} {1}'", compilerProcess.StartInfo.FileName,
                            compilerProcess.StartInfo.Arguments);
                        compilerProcess.Start();
                        compilerProcess.WaitForExit();
                        if (compilerProcess.ExitCode != 0)
                        {
                            Console.WriteLine("Compile Failed (exit code {0})", compilerProcess.ExitCode);
                            return 1;
                        }
                    }
                }

                Console.WriteLine("TotalTime Time: {0} secs",
                    (float)(Stopwatch.GetTimestamp() - startTime) / (float)Stopwatch.Frequency);
                return 0;
            }
            catch(ErrorMessageException e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                return 1;
            }
            catch(AlreadyPrintedErrorException)
            {
                return 1;
            }
        }

        public static string GetAssemblyPackageName(string assemblyName)
        {
            lock(config.assemblyPackageOverrides)
            {
                string packageName;
                if(config.assemblyPackageOverrides.TryGetValue(assemblyName, out packageName))
                {
                    return packageName;
                }
                return assemblyName.Replace('.', '_');
            }
        }

        static String FindConversionRootAndConfigFile()
        {
            conversionRoot = Environment.CurrentDirectory;
            while (true)
            {
                String configFile = Path.Combine(conversionRoot, "cs2d.config");
                if (File.Exists(configFile))
                {
                    return configFile;
                }
                var newConversionRoot = Path.GetDirectoryName(conversionRoot);
                if (String.IsNullOrEmpty(newConversionRoot) || newConversionRoot.Equals(conversionRoot))
                {
                    throw new ErrorMessageException(String.Format(
                        "no cs2d.config file found at or above '{0}'", Environment.CurrentDirectory));
                }
                conversionRoot = newConversionRoot;
            }
        }

        static String FindProgram(String name)
        {
            string exeName   = name + ".exe";
            string batchName = name + ".bat";
            string cmdName   = name + ".cmd";

            string PATH = Environment.GetEnvironmentVariable("PATH");
            int startOfNextPath = 0;
            for(int i = 0; ; i++)
            {
                if(i >= PATH.Length || PATH[i] == ';')
                {
                    if (i > startOfNextPath)
                    {
                        String exePath = PATH.Substring(startOfNextPath, i - startOfNextPath);
                        {
                            String exeNameFullPath = Path.Combine(exePath, exeName);
                            if (File.Exists(exeNameFullPath))
                            {
                                return exeNameFullPath;
                            }
                        }
                        {
                            String batchNameFullPath = Path.Combine(exePath, batchName);
                            if (File.Exists(batchNameFullPath))
                            {
                                return batchNameFullPath;
                            }
                        }
                        {
                            String cmdNameFullPath = Path.Combine(exePath, cmdName);
                            if (File.Exists(cmdNameFullPath))
                            {
                                return cmdNameFullPath;
                            }
                        }
                    }

                    if (i >= PATH.Length)
                    {
                        return null;
                    }
                    startOfNextPath = i + 1;
                }
            }
        }
    }
    static class TaskManager
    {
        static Queue<Task> taskQueue = new Queue<Task>(128);
        public static void AddTask(Task task)
        {
            lock (taskQueue)
            {
                taskQueue.Enqueue(task);
            }
        }
        // Returns: true on success, false if a task failed
        public static bool WaitLoop()
        {
            bool success = true;

            while (true)
            {
                Task task;
                lock (taskQueue)
                {
                    if(taskQueue.Count == 0)
                    {
                        return success;
                    }
                    task = taskQueue.Dequeue();
                }
                try
                {
                    task.Wait();
                }
                catch(AggregateException e)
                {
                    success = false;
                    ErrorMessageException errorMessageException = e.InnerException as ErrorMessageException;
                    if (errorMessageException != null)
                    {
                        Console.WriteLine("Error: {0}", errorMessageException.Message);
                    }
                    else if (e.InnerException is AlreadyPrintedErrorException)
                    {
                    }
                    else
                    {
                        Console.WriteLine(e.InnerException);
                    }
                }
            }
        }
    }

    public static class Helper
    {
        public static Boolean HasItems<T>(this IReadOnlyCollection<T> list)
        {
            return list != null && list.Count > 0;
        }

        public static string GetIdentifierUsingVisitor(this NameSyntax nameSyntax)
        {
            return NameSyntaxIDVisitor.Instance.Visit(nameSyntax);
        }
        class NameSyntaxIDVisitor : CSharpSyntaxVisitor<String>
        {
            public static readonly NameSyntaxIDVisitor Instance = new NameSyntaxIDVisitor();
            private NameSyntaxIDVisitor() { }
            public override string DefaultVisit(SyntaxNode node)
            {
                throw new Exception(String.Format("NameSyntaxIDVisitor type {0} not implemented", node.GetType().Name));
            }
            public override string VisitIdentifierName(IdentifierNameSyntax node)
            {
                return node.Identifier.Text;
            }
            public override string VisitQualifiedName(QualifiedNameSyntax node)
            {
                String resolvedName = String.Format("{0}.{1}", node.Left.GetIdentifierUsingVisitor(), node.Right.Identifier.Text);
                //Console.WriteLine("[{0}] Resolved qualified name to '{1}'",
                //    Thread.CurrentThread.ManagedThreadId, resolvedName);
                return resolvedName;
            }
        }
    }

    class NamespaceMultiplexVisitor : CSharpSyntaxVisitor
    {
        readonly CSharpFileModel csharpFileModel;
        readonly Stack<string> currentNamespaceHeirarchy = new Stack<string>();
        public NamespaceMultiplexVisitor(CSharpFileModel csharpFileModel)
        {
            this.csharpFileModel = csharpFileModel;
        }

        public override void DefaultVisit(SyntaxNode node)
        {
            throw new InvalidOperationException(String.Format(
                "NamespaceMultiplexVisitor.Visit '{0}'", node.GetType().Name));
        }

        public override void VisitCompilationUnit(CompilationUnitSyntax node)
        {
            // Only handle these items in the root context (no namespace)
            // Because these items inside other namespaces will be handled later
            if (currentNamespaceHeirarchy.Count == 0)
            {
                if (node.AttributeLists.Count > 0)
                {
                    foreach (AttributeListSyntax attributeList in node.AttributeLists)
                    {
                        csharpFileModel.containingProject.Add(csharpFileModel, "", attributeList);
                    }
                }
                if (node.Externs.HasItems())
                {
                    throw new NotImplementedException();
                }
            }
            foreach (var member in node.Members)
            {
                Visit(member);
            }
        }

        void VisitMemberDeclaration(MemberDeclarationSyntax memberDecl)
        {
            // Only handle these items in the root context (no namespace)
            // Because these items inside other namespaces will be handled later
            if (currentNamespaceHeirarchy.Count == 0)
            {
                csharpFileModel.containingProject.Add(csharpFileModel, "", memberDecl);
            }
        }
        public override void VisitEnumDeclaration(EnumDeclarationSyntax enumDecl)
        {
            VisitMemberDeclaration(enumDecl);
        }
        public override void VisitClassDeclaration(ClassDeclarationSyntax classDecl)
        {
            VisitMemberDeclaration(classDecl);
        }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax interfaceDecl)
        {
            VisitMemberDeclaration(interfaceDecl);
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax structDecl)
        {
            VisitMemberDeclaration(structDecl);
        }
        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax delegateDecl)
        {
            VisitMemberDeclaration(delegateDecl);
        }

        string CreateNamespaceInCurrentContext(string subNamespace)
        {
            if (currentNamespaceHeirarchy.Count == 0)
            {
                return subNamespace;
            }
            else
            {
                string currentNamespace = currentNamespaceHeirarchy.Peek();
                int length = currentNamespace.Length + 1 + subNamespace.Length;
                StringBuilder builder = new StringBuilder(length);
                builder.Append(currentNamespace);
                builder.Append('.');
                builder.Append(subNamespace);
                Debug.Assert((uint)builder.Length == length);
                return builder.ToString();
            }
        }

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax namespaceDecl)
        {
            string @namespace = CreateNamespaceInCurrentContext(namespaceDecl.Name.GetIdentifierUsingVisitor());
            csharpFileModel.containingProject.Add(csharpFileModel, @namespace, namespaceDecl);

            currentNamespaceHeirarchy.Push(@namespace);
            try
            {
                foreach(MemberDeclarationSyntax memberDecl in namespaceDecl.Members)
                {
                    Visit(memberDecl);
                }
            }
            finally
            {
                currentNamespaceHeirarchy.Pop();
            }
        }
    }
}
