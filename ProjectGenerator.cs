using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToD
{
#if COMMENT
    struct NamespaceFileModelNodes : IComparable<NamespaceFileModelNodes>
    {
        public readonly string @namespace;
        public readonly CSharpFileModelNodes[] fileModels;
        public NamespaceFileModelNodes(string @namespace, CSharpFileModelNodes[] fileModels)
        {
            this.@namespace = @namespace;
            this.fileModels = fileModels;
        }
        public int CompareTo(NamespaceFileModelNodes other)
        {
            return this.@namespace.CompareTo(other.@namespace);
        }
    }

    class NamespaceTreeNode : IComparable<NamespaceTreeNode>
    {
        public readonly string fullNamespace;
        public readonly string name;
        public CSharpFileModelNodes[] fileModelNodesArray;
        public readonly List<NamespaceTreeNode> subnodes = new List<NamespaceTreeNode>();
        public NamespaceTreeNode(string fullNamespace, string name)
        {
            this.fullNamespace = fullNamespace;
            this.name = name;
        }
        public void Sort()
        {
            subnodes.Sort();
            for(int i = 0; i < subnodes.Count; i++)
            {
                subnodes[i].Sort();
            }
        }
        public int CompareTo(NamespaceTreeNode other)
        {
            return name.CompareTo(other.name);
        }
        public void Add(string @namespace, uint namespaceOffset, CSharpFileModelNodes[] fileModelNodesArray)
        {
            if(namespaceOffset >= (uint)@namespace.Length)
            {
                Debug.Assert(this.fileModelNodesArray == null);
                this.fileModelNodesArray = fileModelNodesArray;
                return;
            }

            uint namespaceNameLimit;
            uint nextNamespaceOffset;
            {
                int firstDotIndex = @namespace.IndexOf('.', (int)namespaceOffset);
                if(firstDotIndex >= 0)
                {
                    namespaceNameLimit = (uint)firstDotIndex;
                    nextNamespaceOffset = (uint)firstDotIndex + 1;
                }
                else
                {
                    namespaceNameLimit = (uint)@namespace.Length;
                    nextNamespaceOffset = (uint)@namespace.Length;
                }
            }
            uint namespaceNameLength = namespaceNameLimit - namespaceOffset;

            for (int i = 0; i < subnodes.Count; i++)
            {
                NamespaceTreeNode subnode = subnodes[i];
                if(subnode.name.Length == namespaceNameLength &&
                    SameNamespaceName(subnode.name, @namespace, namespaceOffset))
                {
                    subnode.Add(@namespace, nextNamespaceOffset, fileModelNodesArray);
                    return;
                }
            }

            NamespaceTreeNode newSubnode = new NamespaceTreeNode(
                @namespace.Substring(0, (int)(namespaceOffset + namespaceNameLength)),
                @namespace.Substring((int)namespaceOffset, (int)namespaceNameLength));
            subnodes.Add(newSubnode);
            newSubnode.Add(@namespace, nextNamespaceOffset, fileModelNodesArray);
        }

        static bool SameNamespaceName(String name, String @namespace, uint namespaceOffset)
        {
            for(int i = 0; i < name.Length; i++)
            {
                if(name[i] != @namespace[(int)namespaceOffset+i])
                {
                    return false;
                }
            }
            return true;
        }

        public void FirstPassVisit(ProjectFirstPassVisitor firstPass)
        {
            if(fileModelNodesArray != null)
            {
                foreach (CSharpFileModelNodes fileModelNodes in fileModelNodesArray)
                {
                    firstPass.currentFileModel = fileModelNodes.fileModel;
                    foreach (MemberDeclarationSyntax decl in fileModelNodes.decls)
                    {
                        firstPass.Visit(decl);
                    }
                }
            }
            for (int i = 0; i < subnodes.Count; i++)
            {
                subnodes[i].FirstPassVisit(firstPass);
            }
        }
        public void GenerateCode(DlangWriter writer, ProjectVisitorGenerator generator)
        {
            generator.currentDeclNamespace = fullNamespace;
            try
            {
                if (fullNamespace.Length > 0)
                {
                    writer.WriteLine("//");
                    writer.WriteLine("// namespace {0}", fullNamespace);
                    writer.WriteLine("//");
                    writer.WriteLine("static class {0}", name);
                    writer.WriteLine("{");
                    writer.Tab();
                }
                if (fileModelNodesArray != null)
                {
                    foreach (CSharpFileModelNodes fileModelNodes in fileModelNodesArray)
                    {
                        generator.currentFileModel = fileModelNodes.fileModel;
                        writer.WriteLine();
                        writer.WriteLine("//");
                        writer.WriteLine("// Source Generated From '{0}'", fileModelNodes.fileModel.document.FilePath);
                        writer.WriteLine("//");
                        foreach (SyntaxList<AttributeListSyntax> attrListList in fileModelNodes.attributeLists)
                        {
                            foreach (AttributeListSyntax attrList in attrListList)
                            {
                                writer.WriteIgnored(attrList.GetText());
                            }
                        }
                        foreach (MemberDeclarationSyntax decl in fileModelNodes.decls)
                        {
                            /*
                            if (log != null)
                            {
                                log.PutLine(String.Format("[{0}] Processing File '{1}'",
                                    Thread.CurrentThread.ManagedThreadId, fileModelNodes.fileModel.document.FilePath));
                            }
                            */
                            generator.Visit(decl);
                        }
                    }
                }

                for (int i = 0; i < subnodes.Count; i++)
                {
                    subnodes[i].GenerateCode(writer, generator);
                }
            }
            finally
            {
                if (fullNamespace.Length > 0)
                {
                    writer.Untab();
                    writer.WriteLine("}} // end of namespace {0}", fullNamespace);
                    writer.WriteLine();
                }
            }
        }
    }

    static class Extensions
    {
        public static T[] ToSortedArray<T>(this IEnumerable<T> enumerable)
        {
            T[] array = System.Linq.Enumerable.ToArray(enumerable);
            Array.Sort(array);
            return array;
        }
    }

    public class ProjectGenerator
    {
        readonly ProjectModels projectModels;
        readonly NamespaceTreeNode namespaceTree;
        public String filenameFullPath;
        public readonly List<string> namespaceFiles = new List<string>();
        public BufferedNativeFileSink log;

        static uint GetNamespaceLevel(String @namespace)
        {
            if(@namespace.Length == 0)
            {
                return 0;
            }
            uint dotCount = 0;
            for(int i = 0; i < @namespace.Length; i++)
            {
                if(@namespace[i] == '.')
                {
                    dotCount++;
                }
            }
            return dotCount + 1;
        }

        public ProjectGenerator(ProjectModels projectModels, Dictionary<string,
            Dictionary<CSharpFileModel, CSharpFileModelNodes>> modelsByNamespaceMap)
        {
            this.projectModels = projectModels;

            this.namespaceTree = new NamespaceTreeNode("", "");
            foreach(var namespaceAndModels in modelsByNamespaceMap)
            {
                // Sort by file so output is same no matter the timing
                CSharpFileModelNodes[] sortedFileModels = namespaceAndModels.Value.Values.ToSortedArray();
                this.namespaceTree.Add(namespaceAndModels.Key, 0, sortedFileModels);
            }
            // Sort by namespace so output is same no matter the timing
            this.namespaceTree.Sort();
        }
        public void GenerateCode()
        {
            if (CSharpToD.log)
            {
                this.log = new BufferedNativeFileSink(NativeFile.Open(
                    Path.Combine(CSharpToD.logDirectory, projectModels.outputName),
                    FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[256]);
            }

            // Determine File Name
            String projectSourceDirectory;
            if (namespaceTree.subnodes.Count == 0)
            {
                projectSourceDirectory = null;
                this.filenameFullPath = Path.Combine(CSharpToD.generatedCodePath, projectModels.outputName + ".d");
            }
            else
            {
                projectSourceDirectory = Path.Combine(CSharpToD.generatedCodePath,
                    projectModels.outputName);
                this.filenameFullPath = Path.Combine(projectSourceDirectory, "package.d");
            }

            ProjectFirstPassVisitor firstPass;
            SynchronizedDirectoryCreator.Create(Path.GetDirectoryName(filenameFullPath));
            Console.WriteLine("Creating D Source File '{0}'...", filenameFullPath);
            using (DlangWriter writer = new DlangWriter(new BufferedNativeFileSink(
                NativeFile.Open(filenameFullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[512])))
            {
                writer.WriteLine("module {0};", projectModels.outputName);
                writer.WriteLine();

                //
                // First Pass: find imports
                //
                firstPass = new ProjectFirstPassVisitor(writer, log);
                this.namespaceTree.FirstPassVisit(firstPass);

                //
                // Write Imports
                //
                foreach (KeyValuePair<String, HashSet<TypeSymbolAndArity>> namespaceAndTypes in firstPass.typeSymbolsByNamespace)
                {
                    String @namespace = namespaceAndTypes.Key;
                    if (@namespace.Length > 0)
                    {
                        writer.WriteLine("import {0}.{1} :", projectModels.outputName, @namespace);
                        writer.Tab();
                        uint typeIndex = 0;
                        uint lastIndex = (uint)namespaceAndTypes.Value.Count - 1;
                        foreach (TypeSymbolAndArity typeSymbol in namespaceAndTypes.Value)
                        {
                            writer.Write(typeSymbol.name);
                            if (typeSymbol.arity > 0)
                            {
                                writer.Write("{0}", typeSymbol.arity);
                            }
                            if (typeIndex < lastIndex)
                            {
                                writer.WriteLine(",");
                            }
                            else
                            {
                                writer.WriteLine(";");
                            }
                            typeIndex++;
                        }
                        writer.Untab();
                    }
                }

                //
                // Generate Code
                //
                {
                    var generator = new ProjectVisitorGenerator(this, writer, firstPass);
                    this.namespaceTree.GenerateCode(writer, generator);
                }
            }
            
            //
            // Generate Namespace Alias Files
            //
            foreach (KeyValuePair<String, HashSet<TypeSymbolAndArity>> namespaceAndTypes in firstPass.typeSymbolsByNamespace)
            {
                String @namespace = namespaceAndTypes.Key;
                if (@namespace.Length > 0)
                {
                    //
                    // Determine Namespace Alias Filename
                    //
                    String namespaceAliasFilename;
                    {
                        Boolean namespaceHasChildNamespaces = false;
                        foreach(string otherNamespace in firstPass.typeSymbolsByNamespace.Keys)
                        {
                            if(otherNamespace.Length > @namespace.Length &&
                                otherNamespace.StartsWith(@namespace))
                            {
                                namespaceHasChildNamespaces = true;
                                break;
                            }
                        }

                        String filenameRelative;
                        if (namespaceHasChildNamespaces)
                        {
                            filenameRelative = Path.Combine(
                                @namespace.Replace('.', Path.DirectorySeparatorChar), "package.d");
                        }
                        else
                        {
                            int lastDotIndex = @namespace.LastIndexOf('.');
                            if (lastDotIndex < 0)
                            {
                                filenameRelative = @namespace + ".d";
                            }
                            else
                            {
                                filenameRelative = Path.Combine(
                                    @namespace.Remove(lastDotIndex).Replace('.', Path.DirectorySeparatorChar),
                                    @namespace.Substring(lastDotIndex + 1) + ".d");
                            }
                        }
                        namespaceAliasFilename = Path.Combine(projectSourceDirectory, filenameRelative);
                        namespaceFiles.Add(namespaceAliasFilename);
                        //Console.WriteLine("[DEBUG] Namespace '{0}' going to file '{1}'", @namespace, filenameFullPath);
                    }

                    SynchronizedDirectoryCreator.Create(Path.GetDirectoryName(namespaceAliasFilename));
                    Console.WriteLine("Creating D Source File '{0}'...", namespaceAliasFilename);
                    using (DlangWriter writer = new DlangWriter(new BufferedNativeFileSink(
                        NativeFile.Open(namespaceAliasFilename, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[512])))
                    {
                        writer.WriteLine("module {0}.{1};", projectModels.outputName, @namespace);
                        writer.WriteLine();
                        writer.WriteLine("static import {0};", projectModels.outputName);
                        writer.WriteLine();
                        foreach (TypeSymbolAndArity typeSymbol in namespaceAndTypes.Value)
                        {
                            if (typeSymbol.arity > 0)
                            {
                                writer.WriteLine("alias {0}{1} = {2}.{3}.{0}{1};",
                                    typeSymbol.name, typeSymbol.arity, projectModels.outputName, @namespace);
                            }
                            else
                            {
                                writer.WriteLine("alias {0} = {1}.{2}.{0};",
                                    typeSymbol.name, projectModels.outputName, @namespace);
                            }
                        }
                    }
                }
            }
        }
    }
#endif
}