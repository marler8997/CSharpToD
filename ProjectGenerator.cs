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
    class ProjectFirstPassVisitor : CSharpSyntaxVisitor
    {
        readonly DlangWriter writer;
        readonly BufferedNativeFileSink log;

        public CSharpFileModel currentFileModel;
        
        readonly HashSet<ITypeSymbol> typesAlreadyAdded = new HashSet<ITypeSymbol>();
        public readonly KeyUniqueValues<string, TypeSymbolAndArity> typeSymbolsByNamespace =
            new KeyUniqueValues<string, TypeSymbolAndArity>(true);

        public readonly KeyValues<string, FileAndTypeDecl> partialTypes =
            new KeyValues<string, FileAndTypeDecl>(true);

        public ProjectFirstPassVisitor(DlangWriter writer, BufferedNativeFileSink log)
        {
            this.writer = writer;
            this.log = log;
        }

        public override void DefaultVisit(SyntaxNode node)
        {
        }
        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            foreach (var member in node.Members)
            {
                Visit(member);
            }
        }

        void AddNewType(String dModule, String dlangTypeIdentifier, UInt32 arity)
        {
            //String dModule = SemanticExtensions.NamespaceToDModule(@namespace);
            typeSymbolsByNamespace.Add(dModule, new TypeSymbolAndArity(dlangTypeIdentifier, arity));
            //Console.WriteLine("Namespace '{0}', from symbol '{1}'", typeSymbol.ContainingModule(), typeSymbol.Name);
            //writer.WriteCommentedLine(String.Format("Namespace '{0}', from symbol '{1}'", typeSymbol.ContainingModule(), typeSymbol.Name));
            if (log != null)
            {
                log.PutLine(String.Format("Module '{0}', from symbol '{1}'", dModule, dlangTypeIdentifier));
            }
        }
        void AddNamespaceFrom(ITypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind == TypeKind.TypeParameter)
            {
                // Ignore TypeParameters (These are Generic Type Parameters)
            }
            else if (typeSymbol.TypeKind == TypeKind.Array)
            {
                AddNamespaceFrom(((IArrayTypeSymbol)typeSymbol).ElementType);
            }
            else if (typeSymbol.TypeKind == TypeKind.Pointer)
            {
                AddNamespaceFrom(((IPointerTypeSymbol)typeSymbol).PointedAtType);
            }
            else
            {
                INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
                uint arity = (namedTypeSymbol == null) ? 0 : (uint)namedTypeSymbol.Arity;
                if (typeSymbol.ContainingType != null)
                {
                    AddNamespaceFrom(typeSymbol.ContainingType);
                }
                else
                {
                    if (!typesAlreadyAdded.Contains(typeSymbol))
                    {
                        typesAlreadyAdded.Add(typeSymbol);

                        if (String.IsNullOrEmpty(typeSymbol.Name))
                        {
                            throw new InvalidOperationException();
                        }

                        throw new NotImplementedException();
                        /*
                        AddNewType(typeSymbol.ContainingModule(), DlangWriter.DotNetToD(
                            TypeContext.Default, typeSymbol), arity);
                        if (log != null)
                        {
                            log.PutLine(String.Format("Adding generic types from {0}.{1}", typeSymbol.ContainingModule(), typeSymbol.Name));
                        }
                            */
                    }
                }
                if (arity > 0)
                {
                    foreach (ITypeSymbol genericTypeArg in namedTypeSymbol.TypeArguments)
                    {
                        AddNamespaceFrom(genericTypeArg);
                    }
                }
            }
        }

        void VisitTypeDeclaration(TypeDeclType typeDeclType, TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl.Modifiers.ContainsPartial())
            {
                partialTypes.Add(typeDecl.Identifier.Text,
                    new FileAndTypeDecl(currentFileModel, typeDecl));
            }
            if (typeDecl.BaseList == null)
            {
                if (typeDeclType == TypeDeclType.Class)
                {
                    AddNewType("System", "DotNetObject", 0);
                }
            }
            else
            {
                if (!typeDecl.BaseList.Types.HasItems())
                {
                    throw new InvalidOperationException();
                }

                if (typeDeclType == TypeDeclType.Class)
                {
                    BaseTypeSyntax firstType = typeDecl.BaseList.Types[0];
                    ITypeSymbol firstTypeSymbol = currentFileModel.semanticModel.GetTypeInfo(firstType.Type).Type;
                    if (firstTypeSymbol.TypeKind != TypeKind.Class)
                    {
                        AddNewType("System", "DotNetObject", 0);
                    }
                }

                foreach (BaseTypeSyntax type in typeDecl.BaseList.Types)
                {
                    TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(type.Type);
                    if (typeInfo.Type == null)
                    {
                        throw new InvalidOperationException();
                    }
                    AddNamespaceFrom(typeInfo.Type);
                }
            }
            foreach (var member in typeDecl.Members)
            {
                Visit(member);
            }

            if (typeDeclType == TypeDeclType.Struct)
            {
                if (typeDecl.BaseList != null)
                {
                    AddNewType("System", "DotNetObject", 0);
                }
            }
        }
        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            VisitTypeDeclaration(TypeDeclType.Class, node);
        }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            VisitTypeDeclaration(TypeDeclType.Interface, node);
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            VisitTypeDeclaration(TypeDeclType.Struct, node);
        }

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax delegateDecl)
        {
            AddNamespaceFrom(currentFileModel.semanticModel.GetTypeInfo(delegateDecl.ReturnType).Type);
            foreach (ParameterSyntax param in delegateDecl.ParameterList.Parameters)
            {
                AddNamespaceFrom(currentFileModel.semanticModel.GetTypeInfo(param.Type).Type);
            }
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax fieldDecl)
        {
            ITypeSymbol fieldType = currentFileModel.semanticModel.GetTypeInfo(fieldDecl.Declaration.Type).Type;
            AddNamespaceFrom(fieldType);
        }
    }
    class ProjectVisitorGenerator : CSharpSyntaxVisitor
    {
        readonly ProjectGenerator generator;
        public readonly DlangWriter writer;
        readonly ProjectFirstPassVisitor firstPass;

        public String currentDeclNamespace;
        public CSharpFileModel currentFileModel;

        readonly Stack<DeclContext> currentDeclContext =
            new Stack<DeclContext>();

        public ProjectVisitorGenerator(ProjectGenerator generator, DlangWriter writer,
            ProjectFirstPassVisitor firstPass)
        {
            this.generator = generator;
            this.writer = writer;
            this.firstPass = firstPass;
        }
        public override void DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException(String.Format("ProjectVisitorGenerator for {0}", node.GetType().Name));
        }
        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            if (node.Externs.HasItems())
            {
                throw new NotImplementedException();
            }
            foreach (var member in node.Members)
            {
                Visit(member);
            }
        }
        static void WriteAttributes(DlangWriter writer, SyntaxList<AttributeListSyntax> attributeLists, bool inline)
        {
            foreach (var attributeList in attributeLists)
            {
                String attributeCode = attributeList.GetText().ToString().Trim();
                if (inline)
                {
                    writer.WriteCommentedInline(attributeCode);
                }
                else
                {
                    writer.WriteCommentedLine(attributeCode);
                }
            }
        }
        bool InStaticContext()
        {
            return currentDeclContext.Count > 0 && currentDeclContext.Peek().isStatic;
        }

        void VisitTypeDeclarationSyntax(TypeDeclType typeDeclType, TypeDeclarationSyntax typeDecl)
        {
            ModifierCategories modifiers = new ModifierCategories(typeDecl.Modifiers);

            if (modifiers.@new || modifiers.@volatile || modifiers.@fixed ||
                modifiers.@const || modifiers.@readonly)
            {
                throw new InvalidOperationException();
            }

            INamedTypeSymbol typeSymbol;
            List<FileAndTypeDecl> partialTypeDecls;

            if (modifiers.partial)
            {
                partialTypeDecls = firstPass.partialTypes[typeDecl.Identifier.Text];
                if (!partialTypeDecls.IsFirst(typeDecl))
                {
                    // Only generate the contents for the first type
                    return;
                }
                typeSymbol = partialTypeDecls[0].fileModel.semanticModel.GetDeclaredSymbol(partialTypeDecls[0].typeDecl);
                foreach (FileAndTypeDecl partialTypeDecl in partialTypeDecls)
                {
                    CSharpFileModel saveModel = currentFileModel;
                    try
                    {
                        currentFileModel = partialTypeDecl.fileModel;
                        WriteAttributes(writer, partialTypeDecl.typeDecl.AttributeLists, false);
                    }
                    finally
                    {
                        currentFileModel = saveModel;
                    }
                }
            }
            else
            {
                typeSymbol = currentFileModel.semanticModel.GetDeclaredSymbol(typeDecl);
                partialTypeDecls = null;
                WriteAttributes(writer, typeDecl.AttributeLists, false);
            }
            if (typeSymbol == null)
            {
                throw new InvalidOperationException("A");
            }

            if (modifiers.dlangVisibility != null)
            {
                writer.Write(modifiers.dlangVisibility);
                writer.Write(" ");
            }
            // Pretty much everything is static since it is in
            // a namespace, if it's not in a namespace, then the
            // static modifier does nothing, so I just include it
            // on everything.
            writer.Write("static ");
            if (modifiers.@abstract)
            {
                writer.Write("abstract ");
            }
            if (modifiers.@sealed)
            {
                writer.Write("final ");
            }

            switch (typeDeclType)
            {
                case TypeDeclType.Class:
                    writer.Write("class ");
                    break;
                case TypeDeclType.Interface:
                    writer.Write("interface ");
                    break;
                case TypeDeclType.Struct:
                    writer.Write("struct ");
                    break;
            }
            WriteTypeDeclName(typeDecl);
            // TODO: loop through base list of all partial classes
            if (typeDeclType != TypeDeclType.Struct)
            {
                // Do not write base list for System.Object
                if (typeSymbol.ContainingNamespace.Name == "System" &&
                    typeSymbol.Name == "Object" &&
                    typeSymbol.Arity == 0)
                {
                    if (typeDecl.BaseList != null)
                    {
                        throw new InvalidOperationException();
                    }
                }
                else
                {
                    WriteBaseList(typeDeclType, typeDecl.BaseList);
                    WriteConstraints(typeDecl);
                }
            }
            writer.WriteLine();
            writer.WriteLine("{");
            /*
            if (modifiers.@static)
            {
                writer.HalfTab();
                writer.WriteLine("static:");
                writer.HalfUntab();
            }
            */
            writer.Tab();
            if (modifiers.@static)
            {
                writer.WriteLine("private this() {} // prevent instantiation");
            }

            try
            {
                currentDeclContext.Push(new DeclContext(typeDecl, typeSymbol, modifiers.@static));
                if (partialTypeDecls == null)
                {
                    foreach (var member in typeDecl.Members)
                    {
                        Visit(member);
                    }
                }
                else
                {
                    foreach (FileAndTypeDecl partialTypeDecl in partialTypeDecls)
                    {
                        CSharpFileModel saveModel = currentFileModel;
                        try
                        {
                            currentFileModel = partialTypeDecl.fileModel;
                            foreach (var member in partialTypeDecl.typeDecl.Members)
                            {
                                Visit(member);
                            }
                        }
                        finally
                        {
                            currentFileModel = saveModel;
                        }
                    }
                }
            }
            finally
            {
                currentDeclContext.Pop();
            }

            writer.Untab();
            writer.WriteLine("}");

            //
            // Generate the boxed class if the struct implements interfaces
            //
            if (typeDeclType == TypeDeclType.Struct)
            {
                if (typeDecl.BaseList != null)
                {
                    if (modifiers.dlangVisibility != null)
                    {
                        writer.Write(modifiers.dlangVisibility);
                        writer.Write(" ");
                    }
                    writer.Write("class ");
                    writer.Write("__Boxed__");
                    WriteTypeDeclName(typeDecl);
                    WriteBaseList(TypeDeclType.Class, typeDecl.BaseList);
                    WriteConstraints(typeDecl);
                    writer.WriteLine();
                    writer.WriteLine("{");
                    writer.Tab();
                    {
                        throw new NotImplementedException();
                        //WriteDlangType(TypeContext.Default, typeSymbol);
                        writer.WriteLine(" value;");
                        writer.WriteLine("alias value this;");
                    }
                    writer.Untab();
                    writer.WriteLine("}");
                }
            }
        }

        void WriteTypeDeclName(TypeDeclarationSyntax typeDecl)
        {
            writer.WriteDlangTypeDeclName(currentDeclNamespace, typeDecl);
            if (typeDecl.TypeParameterList != null)
            {
                WriteGenericTypeListDecl(typeDecl.TypeParameterList);
            }
        }
        void WriteGenericTypeListDecl(TypeParameterListSyntax genericTypeList)
        {
            writer.Write("(");
            bool atFirst = true;
            foreach (TypeParameterSyntax typeParam in genericTypeList.Parameters)
            {
                if (atFirst) { atFirst = false; } else { writer.Write(","); }
                writer.Write(typeParam.Identifier.Text);
            }
            writer.Write(")");
        }
        void WriteBaseList(TypeDeclType typeDeclType, BaseListSyntax baseList)
        {
            if (baseList == null)
            {
                if (typeDeclType == TypeDeclType.Class)
                {
                    writer.Write(" : DotNetObject");
                }
            }
            else
            {
                if (!baseList.Types.HasItems())
                {
                    throw new InvalidOperationException();
                }

                writer.Write(" : ");

                bool atFirst = true;
                if (typeDeclType == TypeDeclType.Class)
                {
                    BaseTypeSyntax firstType = baseList.Types[0];
                    ITypeSymbol firstTypeSymbol = currentFileModel.semanticModel.GetTypeInfo(firstType.Type).Type;
                    if (firstTypeSymbol.TypeKind != TypeKind.Class)
                    {
                        writer.Write("DotNetObject");
                        atFirst = false;
                    }
                }

                foreach (BaseTypeSyntax type in baseList.Types)
                {
                    if (atFirst) { atFirst = false; } else { writer.Write(", "); }

                    TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(type.Type);
                    if (typeInfo.Type == null)
                    {
                        throw new InvalidOperationException();
                    }

                    INamedTypeSymbol namedType = (INamedTypeSymbol)typeInfo.Type;
                    throw new NotImplementedException();
                    //WriteDlangType(TypeContext.Default, namedType);
                }
            }
        }
        void WriteConstraints(TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl.ConstraintClauses.HasItems())
            {
                foreach (TypeParameterConstraintClauseSyntax constraint in typeDecl.ConstraintClauses)
                {
                    writer.WriteCommentedInline(constraint.GetText().ToString().Trim());
                }
            }
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            VisitTypeDeclarationSyntax(TypeDeclType.Class, node);
        }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            VisitTypeDeclarationSyntax(TypeDeclType.Interface, node);
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            VisitTypeDeclarationSyntax(TypeDeclType.Struct, node);
        }
        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax delegateDecl)
        {
            WriteAttributes(writer, delegateDecl.AttributeLists, false);

            ModifierCategories modifiers = new ModifierCategories(delegateDecl.Modifiers);
            if (modifiers.@abstract || modifiers.@sealed || modifiers.@partial ||
                modifiers.@new || modifiers.@volatile || modifiers.@fixed ||
                modifiers.@static || modifiers.@const || modifiers.@readonly)
            {
                throw new InvalidOperationException();
            }
            if (modifiers.dlangVisibility != null)
            {
                writer.Write(modifiers.dlangVisibility);
                writer.Write(" ");
            }

            String dlangIdentifier = GetIdentifierName(delegateDecl.Identifier.Text);

            Boolean isGeneric = delegateDecl.Arity > 0;
            if (isGeneric)
            {
                writer.Write("template {0}{1}", dlangIdentifier, delegateDecl.Arity);
                WriteGenericTypeListDecl(delegateDecl.TypeParameterList);

                if (delegateDecl.ConstraintClauses.HasItems())
                {
                    writer.WriteCommentedInline("todo: constraints");
                }

                writer.WriteLine();
                writer.WriteLine("{");
                writer.Tab();
            }

            writer.Write("alias ");
            writer.Write(dlangIdentifier);
            if (isGeneric)
            {
                writer.Write("{0}", delegateDecl.Arity);
            }

            writer.Write(" = ");

            TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(delegateDecl.ReturnType);
            throw new NotImplementedException();
            //writer.WriteDlangType(currentDeclContext, TypeContext.Return, typeInfo.Type);
            writer.Write(" delegate(");

            bool atFirst = true;
            foreach (ParameterSyntax param in delegateDecl.ParameterList.Parameters)
            {
                if (atFirst) { atFirst = false; } else { writer.Write(", "); }
                WriteParameter(param);
            }

            writer.WriteLine(");");

            if (isGeneric)
            {
                writer.Untab();
                writer.WriteLine("}");
            }
        }

        void WriteParameter(ParameterSyntax param)
        {
            //writer.WriteCommentedInline("param");
            WriteAttributes(writer, param.AttributeLists, true);
            ParamModifiers modifiers = new ParamModifiers(param.Modifiers);

            if (modifiers.refout != null)
            {
                writer.Write(modifiers.refout);
                writer.Write(" ");
            }

            ITypeSymbol typeSymbol = currentFileModel.semanticModel.GetTypeInfo(param.Type).Type;
            throw new NotImplementedException();
            //writer.WriteDlangType(currentDeclContext, TypeContext.Default, typeSymbol);

            writer.Write(" ");
            writer.Write(param.Identifier.Text);
            if (param.Default != null)
            {
                throw new NotImplementedException();
            }
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax enumDecl)
        {
            foreach (AttributeListSyntax attrList in enumDecl.AttributeLists)
            {
                writer.WriteIgnored(attrList.GetText());
            }

            ModifierCategories modifiers = new ModifierCategories(enumDecl.Modifiers);
            if (modifiers.@abstract || modifiers.partial || modifiers.@sealed ||
                modifiers.@const || modifiers.@readonly || modifiers.@static || modifiers.@new ||
                modifiers.@unsafe || modifiers.@volatile || modifiers.@fixed)
            {
                throw new InvalidOperationException();
            }
            if (modifiers.dlangVisibility != null)
            {
                writer.Write(modifiers.dlangVisibility);
                writer.Write(" ");
            }

            writer.Write("enum ");
            writer.Write(enumDecl.Identifier.Text);
            if (enumDecl.BaseList != null)
            {
                writer.WriteCommentedInline("todo: implement : <base-type>");
                //WriteBaseList(enumDecl.BaseList);
            }
            if (enumDecl.Members.Count == 0)
            {
                writer.WriteLine("{__no_values__}");
            }
            else
            {
                writer.WriteLine();
                writer.WriteLine("{");
                writer.Tab();
                foreach (EnumMemberDeclarationSyntax enumMember in enumDecl.Members)
                {
                    foreach (AttributeListSyntax attrList in enumMember.AttributeLists)
                    {
                        writer.WriteIgnored(attrList.GetText());
                    }
                    writer.Write(GetIdentifierName(enumMember.Identifier.Text));
                    if (enumMember.EqualsValue != null)
                    {
                        writer.WriteCommentedInline("todo: implement = expression");
                        //writer.Write(" = ");
                        //WriteExpression(enumMember.EqualsValue.Value);
                    }
                    writer.WriteLine(",");
                }
                writer.Untab();
                writer.WriteLine("}");
            }
        }

        // Maps identifers in C# that are keywords in D, to their respective recommeneded translation.
        static readonly Dictionary<string, string> IdentiferMap = new Dictionary<string, string>
        {
            {"function", "function_" },
            {"version", "version_" },
            {"debug", "debug_" },
            {"scope", "scope_" },
        };
        static String GetIdentifierName(String name)
        {
            String mappedName;
            if (IdentiferMap.TryGetValue(name, out mappedName))
            {
                return mappedName;
            }
            return name;
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax fieldDecl)
        {
            //writer.WriteCommentedLine("TODO: generate field");
            foreach (AttributeListSyntax attrList in fieldDecl.AttributeLists)
            {
                writer.WriteIgnored(attrList.GetText());
            }

            ModifierCategories modifiers = new ModifierCategories(fieldDecl.Modifiers);

            if (modifiers.partial || modifiers.@abstract || modifiers.@sealed)
            {
                throw new SyntaxNodeException(fieldDecl, "Invalid modifier for field");
            }

            if (modifiers.dlangVisibility != null)
            {
                writer.Write(modifiers.dlangVisibility);
                writer.Write(" ");
            }
            if (currentDeclContext.Count > 0)
            {
                writer.Write("static ");
            }
            if (modifiers.@const)
            {
                writer.Write("immutable "); // TODO: not sure if this is equivalent
            }
            if (modifiers.@readonly)
            {
                writer.Write("const "); // TODO: not sure if this is equivalent
            }
            if (modifiers.@new)
            {
                writer.WriteCommentedInline("todo: new modifier");
            }
            if (modifiers.@volatile)
            {
                writer.WriteCommentedInline("todo: volatile");
            }
            if (modifiers.@fixed)
            {
                writer.WriteCommentedInline("todo: fixed modifier");
            }

            ITypeSymbol typeSymbol = currentFileModel.semanticModel.GetTypeInfo(fieldDecl.Declaration.Type).Type;
            throw new NotImplementedException();
            //writer.WriteDlangType(currentDeclContext, TypeContext.Default, typeSymbol);
            writer.Write(" ");

            bool atFirst = true;
            foreach (VariableDeclaratorSyntax variableDecl in fieldDecl.Declaration.Variables)
            {
                if (atFirst) { atFirst = false; } else { writer.Write(", "); }

                {
                    String identifierName = GetIdentifierName(variableDecl.Identifier.Text);
                    if (typeSymbol.DlangTypeStringEqualsIdentifier(identifierName))
                    {
                        identifierName = identifierName + "_";
                    }
                    writer.Write(identifierName);
                }

                if (variableDecl.ArgumentList != null)
                {
                    writer.WriteCommentedInline(String.Format("todo: implement field ArgumentList '{0}'",
                        variableDecl.ArgumentList.GetText().ToString().Trim()));
                }
                if (variableDecl.Initializer != null)
                {
                    writer.WriteCommentedInline("todo: implement initializer");
                }
            }
            writer.WriteLine(";");
        }

        void WriteExpression(ExpressionSyntax expression)
        {
            throw new NotImplementedException();
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            writer.WriteCommentedLine("TODO: generate constructor");
        }
        public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node)
        {
            writer.WriteCommentedLine("TODO: generate destructor");
        }
        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            writer.WriteCommentedLine(String.Format("TODO: generate method {0}", node.Identifier));
        }
        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            writer.WriteCommentedLine(String.Format("TODO: generate property '{0}'", node.Identifier));
        }
        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            writer.WriteCommentedLine("TODO: generate indexer");
        }
        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            writer.WriteCommentedLine("TODO: generate operator");
        }
        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            writer.WriteCommentedLine("TODO: generate conversion operator");
        }
        public override void VisitEventDeclaration(EventDeclarationSyntax node)
        {
            writer.WriteCommentedLine(String.Format("TODO: generate event '{0}'", node.Identifier));
        }
        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            writer.WriteCommentedLine("TODO: generate event field");
        }
    }
#endif
}