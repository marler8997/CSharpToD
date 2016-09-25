using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToD
{
    public static class WorkspaceModels
    {
        static readonly Dictionary<string, DlangGenerator> NamespaceGeneratorMap
            = new Dictionary<string, DlangGenerator>();
        internal static IEnumerable<DlangGenerator> Generators { get { return NamespaceGeneratorMap.Values; } }

        // Assumption: called inside lock(NamespaceGeneratorMap)
        static DlangGenerator GetOrCreatGenerator()
        {
            return GetOrCreatGenerator("");
        }
        // Assumption: called inside lock(NamespaceGeneratorMap)
        static DlangGenerator GetOrCreatGenerator(String @namespace)
        {
            DlangGenerator generator;
            if (!NamespaceGeneratorMap.TryGetValue(@namespace, out generator))
            {
                generator = new DlangGenerator(@namespace);
                NamespaceGeneratorMap.Add(@namespace, generator);
                //Console.WriteLine("[DEBUG] New Namespace '{0}'", @namespace);
            }
            return generator;

        }

        public static void AddAttributeLists(CSharpFileModel fileModel, SyntaxList<AttributeListSyntax> attributeLists)
        {
            lock (NamespaceGeneratorMap)
            {
                GetOrCreatGenerator().AddAttributeLists(fileModel, attributeLists);
            }
        }
        public static void AddDecl(CSharpFileModel fileModel, String @namespace, MemberDeclarationSyntax node)
        {
            lock (NamespaceGeneratorMap)
            {
                GetOrCreatGenerator(@namespace).AddDecl(fileModel, node);
            }
        }

        // No need to lock in this methid, all other tasks should be done
        public static void AddCodeGenerationTasks(List<IncludeSource> includeSources)
        {
            // Add Include Sources
            foreach (IncludeSource includeSource in includeSources)
            {
                GetOrCreatGenerator(includeSource.@namespace).AddIncludeFile(includeSource.filename);
            }

            // Determine generator filenames
            // If a module will have submodules, then it will need to
            // be put in a package file
            foreach (var generator in NamespaceGeneratorMap.Values)
            {
                var @namespace = generator.@namespace;
                if (@namespace.Length > 0)
                {
                    foreach (var compare in NamespaceGeneratorMap.Values)
                    {
                        if (generator != compare && compare.@namespace.StartsWith(@namespace))
                        {
                            generator.SetPutInPackage(true);
                            break;
                        }
                    }
                }
            }

            foreach (var generator in NamespaceGeneratorMap.Values)
            {
                var task = new Task(generator.Finish);
                task.Start();
                TaskManager.AddTask(task);
            }
        }
    }

    public class ProjectModels
    {
        public readonly String fileFullPath;
        public readonly String projectDirectory;
        public Project project;

        readonly object fileToProcessLockObject = new object();
        uint filesToProcess;

        public ProjectModels(String fileFullPath)
        {
            this.fileFullPath = fileFullPath;
            this.projectDirectory = Path.GetDirectoryName(fileFullPath);
        }
        public void ProjectLoaded(Task<Project> task)
        {
            project = task.Result;

            Console.WriteLine("[{0}] Loaded project '{1}'", Thread.CurrentThread.ManagedThreadId, fileFullPath);

            //
            // Check Source Defines
            //
            if(CSharpToD.config.sourceDefines.Count > 0)
            {
                CSharpParseOptions parseOptions = (CSharpParseOptions)project.ParseOptions;
                var preprocessorSymbolNames = parseOptions.PreprocessorSymbolNames;
                List<string> newSymbolNames = null;
                foreach (string configured in CSharpToD.config.sourceDefines)
                {
                    if(!System.Linq.Enumerable.Contains(preprocessorSymbolNames, configured))
                    {
                        if(newSymbolNames == null)
                        {
                            newSymbolNames = new List<string>();
                            newSymbolNames.AddRange(preprocessorSymbolNames);
                        }
                        Console.WriteLine("[{0}] [DEBUG] Adding SourceDefine '{1}'",
                            Thread.CurrentThread.ManagedThreadId, configured);
                        newSymbolNames.Add(configured);
                    }
                }
                if(newSymbolNames != null)
                {
                    project = project.WithParseOptions(parseOptions.WithPreprocessorSymbols(newSymbolNames.ToArray()));
                }
            }

            //
            // Start tasks to process the project source files
            //
            this.filesToProcess = (uint)System.Linq.Enumerable.Count(project.Documents);
            foreach (Document document in project.Documents)
            {
                if(CSharpToD.printSourceFiles)
                {
                    Console.WriteLine("Source File: {0}", document.FilePath);
                }
                var fileModel = new CSharpFileModel(this, document);
                //Console.WriteLine("[{0}] Starting syntax loader for '{1}'...",
                //    Thread.CurrentThread.ManagedThreadId, relativePathName);

                TaskManager.AddTask(document.GetSyntaxTreeAsync().ContinueWith(fileModel.SyntaxTreeLoaded));
            }
        }

        internal void FileProcessed()
        {
            lock (fileToProcessLockObject)
            {
                if (filesToProcess == 0)
                {
                    throw new InvalidOperationException("CodeBug");
                }
                filesToProcess--;
            }
            if (filesToProcess == 0)
            {
                Console.WriteLine("[{0}] All {1} files in project '{2}' have been processed",
                    Thread.CurrentThread.ManagedThreadId, project.DocumentIds.Count, project.FilePath);
                /*
                Task finishGeneratorsTask = new Task(FinishGenerators);
                finishGeneratorsTask.Start();
                TaskManager.AddTask(finishGeneratorsTask);
                */
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

        void Validate(String stage, IEnumerable<Diagnostic> diagnostics)
        {
            int errorCount = 0;
            int warningCount = 0;
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    if (!CSharpToD.ignoreFileErrors)
                    {
                        Console.WriteLine("{0}Error: {1}: {2}", stage, document.FilePath, diagnostic.GetMessage());
                        errorCount++;
                    }
                }
                else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                {
                    if (!CSharpToD.noWarnings)
                    {
                        Console.WriteLine("{0}Warning: {1}: {2}", stage, document.FilePath, diagnostic.GetMessage());
                    }
                    warningCount++;
                }
                else if (diagnostic.Severity != DiagnosticSeverity.Hidden)
                {
                    Console.WriteLine("{0}: {1}: {2}", diagnostic.Severity, document.FilePath, diagnostic.GetMessage());
                }
            }
            if (errorCount > 0)
            {
                throw new AlreadyPrintedErrorException();
            }
        }

        public void SyntaxTreeLoaded(Task<SyntaxTree> task)
        {
            //Console.WriteLine("[{0}] [DEBUG] Syntax tree loaded for '{1}'",
            //    Thread.CurrentThread.ManagedThreadId, document.FilePath);
            this.syntaxTree = task.Result;
            Validate("Syntax", syntaxTree.GetDiagnostics());

            TaskManager.AddTask(document.GetSemanticModelAsync().ContinueWith(SemanticTreeLoaded));
        }
        public void SemanticTreeLoaded(Task<SemanticModel> task)
        {
            //Console.WriteLine("[{0}] Semantic model loaded for '{1}'",
            //    Thread.CurrentThread.ManagedThreadId, document.FilePath);
            this.semanticModel = task.Result;
            Validate("Semantic", semanticModel.GetDiagnostics());

            new NamespaceMultiplexVisitor(this).Visit(syntaxTree.GetRoot());
            //Console.WriteLine("[{0}] Done processing '{1}'",
            //    Thread.CurrentThread.ManagedThreadId, document.FilePath);
            containingProject.FileProcessed();
        }
    }
}
