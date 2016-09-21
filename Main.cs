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

        public static String conversionRoot;
        public static String generatedCodePath;

        static void Usage()
        {
            Console.WriteLine("csharptod [--dir <project-root>]");
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
                    else
                    {
                        throw new ErrorMessageException(String.Format("Unknown option: {0}", arg));
                    }
                }
                else if (arg.StartsWith("-"))
                {
                    throw new ErrorMessageException(String.Format("Unknown option: {0}", arg));
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
                Config config = new Config(configFile);

                if(config.projects.Count == 0)
                {
                    Console.WriteLine("There are no projects configured");
                    return 0;
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
                        projectModelsArray[projectIndex] = new ProjectModels(projectFileFullPath);
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

                MSBuildWorkspace workspace = MSBuildWorkspace.Create();
                workspace.LoadMetadataForReferencedProjects = true;


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
                TaskManager.WaitLoop();

                //
                // Start Code Generation
                //
                Console.WriteLine("==================== Starting Code Generation =====================");
                WorkspaceModels.AddCodeGenerationTasks(config.includeSources);

                // Wait for all files in all code to be generated
                TaskManager.WaitLoop();

                DBuildGenerator.MakeDProjectFile(config, projectModelsArray);

                Console.WriteLine("TotalTime Time: {0} secs",
                    (float)(Stopwatch.GetTimestamp() - startTime) / (float)Stopwatch.Frequency);
                return 0;
            }
            catch(ErrorMessageException e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                return 1;
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
        public static void WaitLoop()
        {
            while (true)
            {
                Task task;
                lock (taskQueue)
                {
                    if(taskQueue.Count == 0)
                    {
                        return;
                    }
                    task = taskQueue.Dequeue();
                }
                task.Wait();
            }
        }
    }


    public class CSharpFileModel
    {
        public readonly ProjectModels containingProject;
        public readonly Document document;
        SyntaxTree syntaxTree;
        public SemanticModel semanticModel;

        public CSharpFileModel(ProjectModels containingProject, Document document)
        {
            this.containingProject = containingProject;
            this.document = document;
        }

        void Validate(IEnumerable<Diagnostic> diagnostics)
        {
            int errorCount = 0;
            int warningCount = 0;
            foreach (Diagnostic diagnostic in syntaxTree.GetDiagnostics())
            {
                errorCount++;
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    Console.WriteLine("{0}: Error: {1}", document.FilePath, diagnostic.GetMessage());
                    errorCount++;
                }
                else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                {
                    Console.WriteLine("{0}: Warning: {1}", document.FilePath, diagnostic.GetMessage());
                    warningCount++;
                }
            }
            if (errorCount > 0)
            {
                throw new ErrorMessageException("SyntaxError(s)");
            }
        }

        public void SyntaxTreeLoaded(Task<SyntaxTree> task)
        {
            //Console.WriteLine("[{0}] [DEBUG] Syntax tree loaded for '{1}'",
            //    Thread.CurrentThread.ManagedThreadId, document.FilePath);
            this.syntaxTree = task.Result;
            Validate(syntaxTree.GetDiagnostics());

            TaskManager.AddTask(document.GetSemanticModelAsync().ContinueWith(SemanticTreeLoaded));
        }
        public void SemanticTreeLoaded(Task<SemanticModel> task)
        {
            //Console.WriteLine("[{0}] Semantic model loaded for '{1}'",
            //    Thread.CurrentThread.ManagedThreadId, document.FilePath);
            this.semanticModel = task.Result;
            Validate(semanticModel.GetDiagnostics());

            new NamespaceMultiplexVisitor(this).Visit(syntaxTree.GetRoot());
            //Console.WriteLine("[{0}] Done processing '{1}'",
            //    Thread.CurrentThread.ManagedThreadId, document.FilePath);
            containingProject.FileProcessed();
        }
    }


    public static class Helper
    {
        public static Boolean IsNullOrEmpty<T>(this IReadOnlyCollection<T> list)
        {
            return list == null || list.Count == 0;
        }
        public static Boolean HasItems<T>(this IReadOnlyCollection<T> list)
        {
            return list != null && list.Count > 0;
        }



        public static string Identifier(this NameSyntax nameSyntax)
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
                String resolvedName = String.Format("{0}.{1}", node.Left.Identifier(), node.Right.Identifier.Text);
                //Console.WriteLine("[{0}] Resolved qualified name to '{1}'",
                //    Thread.CurrentThread.ManagedThreadId, resolvedName);
                return resolvedName;
            }
        }
    }

    class NamespaceMultiplexVisitor : CSharpSyntaxVisitor
    {
        readonly CSharpFileModel csharpFileModel;
        public NamespaceMultiplexVisitor(CSharpFileModel csharpFileModel)
        {
            this.csharpFileModel = csharpFileModel;
        }
        public override void DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException(String.Format("NamespaceMultiplexVisitor for {0}", node.GetType().Name));
        }
        public override void VisitCompilationUnit(CompilationUnitSyntax node)
        {
            if (node.AttributeLists.Count > 0)
            {
                WorkspaceModels.AddAttributeLists(csharpFileModel, node.AttributeLists);
            }
            if (node.Externs.HasItems())
            {
                throw new NotImplementedException();
            }
            //
            // Ignore node.Usings...all info from usings is available in the semantic model
            //
            foreach (var member in node.Members)
            {
                Visit(member);
            }
        }
        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            string @namespace = node.Name.Identifier();
            WorkspaceModels.AddNamespace(csharpFileModel, node);
        }
    }
}
