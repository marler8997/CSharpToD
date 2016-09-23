using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToD
{
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
}
