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

namespace CS2D
{
    class CS2DMain
    {
        public static String projectDirectory;
        public static String dlangDirectory;

        static void Usage()
        {
            Console.WriteLine("cs2d <project-file>");
        }
        static int Main(string[] args)
        {
            if(args.Length == 0)
            {
                Usage();
                return 0;
            }
            if(args.Length != 1)
            {
                Console.WriteLine("Error: too many command line arguments");
                return 1;
            }
            String projectFile = args[0];
            if(!File.Exists(projectFile))
            {
                Console.WriteLine("Error: \"{0}\" does not exist", projectFile);
                return 1;
            }
            projectDirectory = Path.GetDirectoryName(projectFile);

            dlangDirectory = Path.Combine(projectDirectory, "dlang");
            if (Directory.Exists(dlangDirectory))
            {
                Console.WriteLine("[{0}] Cleaning '{1}'", Thread.CurrentThread.ManagedThreadId, dlangDirectory);
                Directory.Delete(dlangDirectory, true);
            }
            Directory.CreateDirectory(dlangDirectory);

            MSBuildWorkspace workspace = MSBuildWorkspace.Create();
            workspace.LoadMetadataForReferencedProjects = true;

            Console.WriteLine("[{0}] Opening '{1}'...", Thread.CurrentThread.ManagedThreadId, projectFile);
            Project project = workspace.OpenProjectAsync(projectFile).Result;

            var beforeLoad = Stopwatch.GetTimestamp();
            foreach (Document document in project.Documents)
            {
                if (!document.FilePath.StartsWith(projectDirectory))
                {
                    Console.WriteLine("Project Directory : {0}", projectDirectory);
                    Console.WriteLine("Source Document   : {0}", document.FilePath);
                    Console.WriteLine("Error: source document did not start with project directory");
                    return 1;
                }
                String relativePathName = document.FilePath.Substring(projectDirectory.Length + 1);

                var fileModel = new CSharpFileModel(document, relativePathName);
                Console.WriteLine("[{0}] Starting syntax loader for '{1}'...",
                    Thread.CurrentThread.ManagedThreadId, relativePathName);
                TaskManager.AddTask(document.GetSyntaxTreeAsync().ContinueWith(fileModel.SyntaxTreeLoaded));
            }

            //
            // Create the D Project File
            //
            //MakeDProjectFile(projectFile, project, documentProcessors);

            TaskManager.WaitLoop();
            Console.WriteLine("Load Time: {0} secs", (float)(Stopwatch.GetTimestamp() - beforeLoad) / (float)Stopwatch.Frequency);

            DlangGenerators.FinishGenerators();
            
            
            return 0;
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
        public readonly Document document;
        public readonly String relativePathAndName;
        SyntaxTree syntaxTree;
        SemanticModel semanticModel;

        public CSharpFileModel(Document document, String relativePathName)
        {
            this.document = document;
            this.relativePathAndName = relativePathName;
            //this.fullDlangFilename = Path.Combine(CS2DMain.dlangDirectory, Path.ChangeExtension(relativePathAndName, "d"));
        }
        public void SyntaxTreeLoaded(Task<SyntaxTree> task)
        {
            Console.WriteLine("[{0}] Syntax tree loaded for '{1}'",
                Thread.CurrentThread.ManagedThreadId, document.FilePath);
            this.syntaxTree = task.Result;
            TaskManager.AddTask(document.GetSemanticModelAsync().ContinueWith(SemanticTreeLoaded));
        }
        public void SemanticTreeLoaded(Task<SemanticModel> task)
        {
            Console.WriteLine("[{0}] Semantic model loaded for '{1}'",
                Thread.CurrentThread.ManagedThreadId, document.FilePath);
            this.semanticModel = task.Result;

            new NamespaceMultiplexVisitor(this).Visit(syntaxTree.GetRoot());
            /*
            DlangDirectoryCreator.SynchronizedCreateDirectoryFor(fullDlangFilename);

            Console.WriteLine("[{0}] Converting file to {1}...", Thread.CurrentThread.ManagedThreadId, fullDlangFilename);
            using (var dlangWriter = new DlangWriter(new BufferedNativeFileSink(
                NativeFile.Open(fullDlangFilename, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[512])))
            {
                new CS2DVisitor(dlangWriter).Visit(syntaxTree.GetRoot());
            }
            Console.WriteLine("[{0}] Done with {1}", Thread.CurrentThread.ManagedThreadId, fullDlangFilename);
            */
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
                throw new Exception(String.Format("visitor of type {0} not implemented", node.GetType()));
            }
            public override string VisitIdentifierName(IdentifierNameSyntax node)
            {
                return node.Identifier.Text;
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
                DlangGenerator generator = DlangGenerators.GetOrCreatGenerator();
                generator.ProcessAttributeLists(csharpFileModel, node.AttributeLists);
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

            DlangGenerator generator = DlangGenerators.GetOrCreatGenerator(@namespace);
            generator.AddNamespace(csharpFileModel, node);
        }
    }
}
