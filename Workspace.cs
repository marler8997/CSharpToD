using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CS2D
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
            lock(NamespaceGeneratorMap)
            {
                GetOrCreatGenerator().AddAttributeLists(fileModel, attributeLists);
            }
        }
        public static void AddNamespace(CSharpFileModel fileModel, NamespaceDeclarationSyntax node)
        {
            lock (NamespaceGeneratorMap)
            {
                GetOrCreatGenerator(node.Name.Identifier()).AddNamespace(fileModel, node);
            }
        }

        // No need to lock in this methid, all other tasks should be done
        public static void AddCodeGenerationTasks(List<IncludeSource> includeSources)
        {
            // Add Include Sources
            foreach(IncludeSource includeSource in includeSources)
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
}
