using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToD
{
    public static class SynchronizedDirectoryCreator
    {
        static readonly Object directoryCreatorLock = new Object();
        public static void Create(String directoryName)
        {
            lock (directoryCreatorLock)
            {
                if (!Directory.Exists(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
            }
        }
    }
    public enum TypeContext
    {
        Default,
        Return,
    }
    public class CSharpFileModelNodes : IComparable<CSharpFileModelNodes>
    {
        public readonly CSharpFileModel fileModel;
        public readonly List<SyntaxList<AttributeListSyntax>> attributeLists = new List<SyntaxList<AttributeListSyntax>>();
        public readonly List<MemberDeclarationSyntax> decls = new List<MemberDeclarationSyntax>();
        public CSharpFileModelNodes(CSharpFileModel fileModel)
        {
            this.fileModel = fileModel;
        }
        public int CompareTo(CSharpFileModelNodes other)
        {
            return fileModel.document.FilePath.CompareTo(other.fileModel.document.FilePath);
        }
    }
    public class DlangGenerator
    {
        public readonly String @namespace;
        public BufferedNativeFileSink log;
        Boolean putInPackage;
        public string filenameFullPath;

        readonly Dictionary<CSharpFileModel, CSharpFileModelNodes> fileModelNodeMap =
            new Dictionary<CSharpFileModel, CSharpFileModelNodes>();
        List<String> includeSourceFiles;

        Dictionary<string, object> typeNameMap = new Dictionary<string, object>();

        public DlangGenerator(String @namespace)
        {
            this.@namespace = @namespace;
        }
        public void SetPutInPackage(bool putInPackage)
        {
            this.putInPackage = putInPackage;
        }

        CSharpFileModelNodes GetFileModelNodes(CSharpFileModel fileModel)
        {
            CSharpFileModelNodes fileModelNodes;
            lock (fileModelNodeMap)
            {
                if (!fileModelNodeMap.TryGetValue(fileModel, out fileModelNodes))
                {
                    fileModelNodes = new CSharpFileModelNodes(fileModel);
                    fileModelNodeMap.Add(fileModel, fileModelNodes);
                }
            }
            return fileModelNodes;
        }
        public void AddAttributeLists(CSharpFileModel fileModel, SyntaxList<AttributeListSyntax> attributeLists)
        {
            GetFileModelNodes(fileModel).attributeLists.Add(attributeLists);
        }
        public void AddDecl(CSharpFileModel fileModel, MemberDeclarationSyntax node)
        {
            GetFileModelNodes(fileModel).decls.Add(node);
        }
        public void AddIncludeFile(String includeFile)
        {
            if (includeSourceFiles == null)
            {
                includeSourceFiles = new List<string>();
            }
            includeSourceFiles.Add(includeFile);
        }

        public void Finish()
        {
            try
            {
                if (CSharpToD.log)
                {
                    this.log = new BufferedNativeFileSink(NativeFile.Open(
                        Path.Combine(CSharpToD.logDirectory, @namespace),
                        FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[256]);
                }

                CSharpFileModelNodes[] fileModelNodesArray = new CSharpFileModelNodes[fileModelNodeMap.Count];
                {
                    int i = 0;
                    foreach (CSharpFileModelNodes fileModelNodes in fileModelNodeMap.Values)
                    {
                        fileModelNodesArray[i++] = fileModelNodes;
                    }
                }
                // Sort the files alphabetically, so that the output stays the same regardless
                // of timing (which file gets processed first).
                Array.Sort(fileModelNodesArray);

                //
                // Determine File Name
                //
                {
                    String filenameRelative;
                    if (@namespace.Length == 0)
                    {
                        filenameRelative = "__NoNamespace__.d";
                    }
                    else if (putInPackage)
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
                    this.filenameFullPath = Path.Combine(CSharpToD.generatedCodePath, filenameRelative);
                    //Console.WriteLine("[DEBUG] Namespace '{0}' going to file '{1}'", @namespace, filenameFullPath);
                }

                SynchronizedDirectoryCreator.Create(Path.GetDirectoryName(filenameFullPath));
                Console.WriteLine("Creating D Source File '{0}'...", filenameFullPath);
                using (DlangWriter writer = new DlangWriter(new BufferedNativeFileSink(
                    NativeFile.Open(filenameFullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[512])))
                {
                    if (@namespace.Length == 0)
                    {
                        writer.WriteLine("// module __NoNamespace__;");
                    }
                    else
                    {
                        writer.WriteLine("module {0};", @namespace);
                    }
                    writer.WriteLine();

                    //
                    // First Pass: find imports
                    //
                    FirstPassVisitor firstPass = new FirstPassVisitor(writer, log);
                    foreach (CSharpFileModelNodes fileModelNodes in fileModelNodesArray)
                    {
                        foreach (MemberDeclarationSyntax decl in fileModelNodes.decls)
                        {
                            firstPass.currentFileModel = fileModelNodes.fileModel;
                            firstPass.Visit(decl);
                        }
                    }

                    //
                    // Add Include Files
                    //
                    if (includeSourceFiles != null)
                    {
                        foreach (String includeSourceFile in includeSourceFiles)
                        {
                            String includeSourceFullPath = Path.Combine(CSharpToD.conversionRoot, includeSourceFile);
                            if (!File.Exists(includeSourceFullPath))
                            {
                                throw new InvalidOperationException(String.Format(
                                    "Include Source does not exist (namespace={0}, file={1})",
                                    @namespace, includeSourceFullPath));
                            }
                            using (FileStream stream = new FileStream(includeSourceFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                writer.Write(stream);
                            }
                        }
                    }

                    foreach (KeyValuePair<String, HashSet<TypeSymbolAndArity>> namespaceAndType in firstPass.typeSymbolsByNamespace)
                    {
                        if (namespaceAndType.Key != this.@namespace)
                        {
                            if (namespaceAndType.Key.Length == 0)
                            {
                                writer.WriteLine("import __NoNamespace__ /*:");
                            }
                            else
                            {
                                writer.WriteLine("import {0} :", namespaceAndType.Key);
                            }
                            writer.Tab();
                            uint typeIndex = 0;
                            uint lastIndex = (uint)namespaceAndType.Value.Count - 1;
                            foreach (TypeSymbolAndArity typeSymbol in namespaceAndType.Value)
                            {
                                writer.Write(typeSymbol.name);
                                if(typeSymbol.arity > 0)
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
                    DlangVisitorGenerator visitor = new DlangVisitorGenerator(this, writer, firstPass);
                    foreach (CSharpFileModelNodes fileModelNodes in fileModelNodesArray)
                    {
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
                            if (log != null)
                            {
                                log.PutLine(String.Format("[{0}] Processing File '{1}'",
                                    Thread.CurrentThread.ManagedThreadId, fileModelNodes.fileModel.document.FilePath));
                            }
                            visitor.currentFileModel = fileModelNodes.fileModel;
                            visitor.Visit(decl);
                        }
                    }
                }
            }
            finally
            {
                if(log != null)
                {
                    log.Dispose();
                }
            }
        }
    }

    struct FileAndTypeDecl
    {
        public readonly CSharpFileModel fileModel;
        public readonly TypeDeclarationSyntax typeDecl;
        public FileAndTypeDecl(CSharpFileModel fileModel, TypeDeclarationSyntax typeDecl)
        {
            this.fileModel = fileModel;
            this.typeDecl = typeDecl;
        }
    }
    static class FileAndTypeDecls
    {
        public static Boolean IsFirst(this List<FileAndTypeDecl> fileAndTypeDecls,
            TypeDeclarationSyntax typeDecl)
        {
            return fileAndTypeDecls[0].typeDecl == typeDecl;
        }
    }

    struct TypeSymbolAndArity
    {
        public readonly String name;
        public readonly UInt32 arity;
        public TypeSymbolAndArity(String name, UInt32 arity)
        {
            this.name = name;
            this.arity = arity;
        }
    }
    enum TypeDeclType
    {
        Class = 0,
        Interface = 1,
        Struct = 2,
    }
    class FirstPassVisitor : CSharpSyntaxVisitor
    {
        readonly DlangWriter writer;
        readonly BufferedNativeFileSink log;
        public CSharpFileModel currentFileModel;
        
        readonly HashSet<ITypeSymbol> typesAlreadyAdded = new HashSet<ITypeSymbol>();
        public readonly KeyUniqueValues<string, TypeSymbolAndArity> typeSymbolsByNamespace =
            new KeyUniqueValues<string, TypeSymbolAndArity>(true);

        public readonly KeyValues<string, FileAndTypeDecl> partialTypes =
            new KeyValues<string, FileAndTypeDecl>(true);

        public FirstPassVisitor(DlangWriter writer, BufferedNativeFileSink log)
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
                return;
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

                        AddNewType(typeSymbol.ContainingModule(), DlangWriter.DotNetToD(
                            TypeContext.Default, typeSymbol), arity);

                        if (log != null)
                        {
                            log.PutLine(String.Format("Adding generic types from {0}.{1}", typeSymbol.ContainingModule(), typeSymbol.Name));
                        }
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
            if(typeDecl.BaseList == null)
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
    public struct DeclContext
    {
        public readonly TypeDeclarationSyntax typeDecl;
        public readonly ITypeSymbol typeSymbol;
        public readonly bool isStatic;
        public DeclContext(TypeDeclarationSyntax typeDecl, ITypeSymbol typeSymbol, bool isStatic)
        {
            this.typeDecl = typeDecl;
            this.typeSymbol = typeSymbol;
            this.isStatic = isStatic;
        }
    }
    class DlangVisitorGenerator : CSharpSyntaxVisitor
    {
        readonly DlangGenerator generator;
        public readonly DlangWriter writer;
        public CSharpFileModel currentFileModel;
        readonly FirstPassVisitor firstPass;

        readonly Stack<DeclContext> currentDeclContext =
            new Stack<DeclContext>();

        public DlangVisitorGenerator(DlangGenerator generator, DlangWriter writer,
            FirstPassVisitor firstPass)
        {
            this.generator = generator;
            this.writer = writer;
            this.firstPass = firstPass;
        }
        public override void DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException(String.Format("DlangVisitorGenerator for {0}", node.GetType().Name));
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

            if(modifiers.@new || modifiers.@volatile || modifiers.@fixed ||
                modifiers.@const || modifiers.@readonly)
            {
                throw new InvalidOperationException();
            }

            INamedTypeSymbol typeSymbol;
            List<FileAndTypeDecl> partialTypeDecls;

            if (modifiers.partial)
            {
                partialTypeDecls = firstPass.partialTypes[typeDecl.Identifier.Text];
                if(!partialTypeDecls.IsFirst(typeDecl))
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
            if(currentDeclContext.Count > 0)
            {
                writer.Write("static ");
            }
            if (modifiers.@abstract)
            {
                writer.Write("abstract ");
            }
            if (modifiers.@sealed)
            {
                writer.Write("final ");
            }

            switch(typeDeclType)
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
                    if(typeDecl.BaseList != null)
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
            if(modifiers.@static)
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
                        writer.WriteDlangType(null, TypeContext.Default, typeSymbol);
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
            writer.WriteDlangTypeDeclName(generator.@namespace, typeDecl);
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
                if(!baseList.Types.HasItems())
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
                    writer.WriteDlangType(currentDeclContext, TypeContext.Default, namedType);
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
            if(isGeneric)
            {
                writer.Write("template {0}{1}", dlangIdentifier, delegateDecl.Arity);
                WriteGenericTypeListDecl(delegateDecl.TypeParameterList);

                if(delegateDecl.ConstraintClauses.HasItems())
                {
                    writer.WriteCommentedInline("todo: constraints");
                }

                writer.WriteLine();
                writer.WriteLine("{");
                writer.Tab();
            }

            writer.Write("alias ");
            writer.Write(dlangIdentifier);
            if(isGeneric)
            {
                writer.Write("{0}", delegateDecl.Arity);
            }

            writer.Write(" = ");

            TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(delegateDecl.ReturnType);
            writer.WriteDlangType(currentDeclContext, TypeContext.Return, typeInfo.Type);
            writer.Write(" delegate(");

            bool atFirst = true;
            foreach(ParameterSyntax param in delegateDecl.ParameterList.Parameters)
            {
                if(atFirst) { atFirst = false; } else { writer.Write(", "); }
                WriteParameter(param);
            }

            writer.WriteLine(");");

            if(isGeneric)
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

            if(modifiers.refout != null)
            {
                writer.Write(modifiers.refout);
                writer.Write(" ");
            }

            ITypeSymbol typeSymbol = currentFileModel.semanticModel.GetTypeInfo(param.Type).Type;
            writer.WriteDlangType(currentDeclContext, TypeContext.Default, typeSymbol);

            writer.Write(" ");
            writer.Write(param.Identifier.Text);
            if(param.Default != null)
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
            if(modifiers.@abstract || modifiers.partial || modifiers.@sealed ||
                modifiers.@const || modifiers.@readonly || modifiers.@static || modifiers.@new ||
                modifiers.@unsafe || modifiers.@volatile || modifiers.@fixed)
            {
                throw new InvalidOperationException();
            }
            if(modifiers.dlangVisibility != null)
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
            if(IdentiferMap.TryGetValue(name, out mappedName))
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

            if(modifiers.partial || modifiers.@abstract || modifiers.@sealed)
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
            if(modifiers.@const)
            {
                writer.Write("immutable "); // TODO: not sure if this is equivalent
            }
            if(modifiers.@readonly)
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
            if(modifiers.@fixed)
            {
                writer.WriteCommentedInline("todo: fixed modifier");
            }
            
            ITypeSymbol typeSymbol = currentFileModel.semanticModel.GetTypeInfo(fieldDecl.Declaration.Type).Type;
            writer.WriteDlangType(currentDeclContext, TypeContext.Default, typeSymbol);
            writer.Write(" ");

            bool atFirst = true;
            foreach (VariableDeclaratorSyntax variableDecl in fieldDecl.Declaration.Variables)
            {
                if (atFirst) { atFirst = false; } else { writer.Write(", "); }

                {
                    String identifierName = GetIdentifierName(variableDecl.Identifier.Text);
                    if(writer.DlangTypeStringEqualsIdentifier(typeSymbol, identifierName))
                    {
                        identifierName = identifierName + "_";
                    }
                    writer.Write(identifierName);
                }

                if(variableDecl.ArgumentList != null)
                {
                    writer.WriteCommentedInline(String.Format("todo: implement field ArgumentList '{0}'",
                        variableDecl.ArgumentList.GetText().ToString().Trim()));
                }
                if(variableDecl.Initializer != null)
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

    public static class Modifiers
    {
        public static bool ContainsPartial(this SyntaxTokenList tokens)
        {
            foreach(SyntaxToken token in tokens)
            {
                if(token.Text == "partial")
                {
                    return true;
                }
            }
            return false;
        }
    }
    public static class SemanticExtensions
    {
        public static String ContainingModule(this ITypeSymbol typeSymbol)
        {
            INamespaceSymbol namespaceSymbol = typeSymbol.ContainingNamespace;
            if (namespaceSymbol == null || namespaceSymbol.Name.Length == 0)
            {
                return "__NoNamespace__";
            }
            return namespaceSymbol.ToString();
        }
        public static String NamespaceToDModule(String @namespace)
        {
            if (@namespace.Length == 0)
            {
                return "__NoNamespace__";
            }
            return @namespace;
        }
        public static Boolean Inside(this Stack<DeclContext> declContextStack, INamedTypeSymbol typeSymbol)
        {
            if(declContextStack == null)
            {
                return false;
            }
            foreach(DeclContext declContext in declContextStack)
            {
                if (declContext.typeSymbol.Equals(typeSymbol))
                {
                    return true;
                }
            }
            return false;
        }
    }

    class SyntaxNodeException : Exception
    {
        public SyntaxNodeException(SyntaxNode node, String message)
            : base(String.Format("{0}: {1}",
                node.GetLocation().GetLineSpan(), message))
        {
        }
        public SyntaxNodeException(SyntaxToken token, String message)
            : base(String.Format("{0}: {1}",
                token.GetLocation().GetLineSpan(), message))
        {
        }
    }


    struct ParamModifiers
    {
        public string refout;
        public ParamModifiers(SyntaxTokenList modifiers)
        {
            this.refout = null;

            foreach (SyntaxToken modifier in modifiers)
            {
                String text = modifier.Text;
                if (text == "ref" || text == "out")
                {
                    if (this.refout != null)
                    {
                        throw new SyntaxNodeException(modifier, String.Format(
                            "parameter ref/out set twice '{0}' and '{1}'", this.refout, text));
                    }
                    this.refout = text;
                }
                else
                {
                    throw new NotImplementedException(String.Format(
                        "Parameter Modifier '{0}' not implemented", text));
                }
            }
        }
        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            bool atFirst = true;
            if (refout != null)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append(refout);
            }
            return builder.ToString();
        }
    }
    struct ModifierCategories
    {
        public string csharpVisibility;
        public string dlangVisibility;

        public bool partial;
        public bool @static;
        public bool @const;
        public bool @readonly;
        public bool @abstract;
        public bool @sealed;
        public bool @unsafe;
        public bool @new;
        public bool @volatile;
        public bool @fixed;

        public ModifierCategories(SyntaxTokenList modifiers)
        {
            this.csharpVisibility = null;
            this.dlangVisibility = null;
            this.partial = false;
            this.@static = false;
            this.@const = false;
            this.@readonly = false;
            this.@abstract = false;
            this.@sealed = false;
            this.@unsafe = false;
            this.@new = false;
            this.@volatile = false;
            this.@fixed = false;

            foreach (SyntaxToken modifier in modifiers)
            {
                String text = modifier.Text;
                if (text == "public")
                {
                    EnforceNoVisibilityYet(modifier, text);
                    this.csharpVisibility = text;
                    this.dlangVisibility = text;
                }
                else if(text == "private" || text == "protected")
                {
                    EnforceNoVisibilityYet(modifier, text);
                    this.csharpVisibility = text;
                    this.dlangVisibility = text;
                }
                else if(text == "protected")
                {
                    if (this.csharpVisibility != null)
                    {
                        if (this.csharpVisibility == "internal")
                        {
                            this.csharpVisibility = "protected internal";
                            this.dlangVisibility = "public"; // Not sure what "protected internal" maps to, just use "public" for now
                        }
                        else
                        {
                            throw new SyntaxNodeException(modifier, String.Format("visibility set twice '{0}' and '{1}'",
                                this.csharpVisibility, text));
                        }
                    }
                    else
                    {
                        this.csharpVisibility = text;
                        this.dlangVisibility = text;
                    }
                }
                else if (text == "internal")
                {
                    if (this.csharpVisibility != null)
                    {
                        if (this.csharpVisibility == "protected")
                        {
                            this.csharpVisibility = "protected internal";
                            this.dlangVisibility = "public "; // Not sure what "protected internal" maps to, just use "public" for now
                        }
                        else
                        {
                            throw new SyntaxNodeException(modifier, String.Format("visibility set twice '{0}' and '{1}'",
                                this.csharpVisibility, text));
                        }
                    }
                    else
                    {
                        this.csharpVisibility = text;
                        this.dlangVisibility = "public"; // treat 'internal' as public
                    }
                }
                else if (text == "static")
                {
                    if (this.@static)
                    {
                        throw new SyntaxNodeException(modifier, "static set twice");
                    }
                    this.@static = true;
                }
                else if(text == "const")
                {
                    if (this.@const)
                    {
                        throw new SyntaxNodeException(modifier, "const set twice");
                    }
                    this.@const = true;
                }
                else if(text == "readonly")
                {
                    if (this.@readonly)
                    {
                        throw new SyntaxNodeException(modifier, "readonly set twice");
                    }
                    this.@readonly = true;
                }
                else if (text == "abstract")
                {
                    if (this.@abstract)
                    {
                        throw new SyntaxNodeException(modifier, "abstract set twice");
                    }
                    this.@abstract = true;
                }
                else if(text == "partial")
                {
                    if(this.partial)
                    {
                        throw new SyntaxNodeException(modifier, "partial set twice");
                    }
                    this.partial = true;
                }
                else if(text == "sealed")
                {
                    if (this.@sealed)
                    {
                        throw new SyntaxNodeException(modifier, "sealed set twice");
                    }
                    this.@sealed = true;
                }
                else if(text == "unsafe")
                {
                    if (this.@unsafe)
                    {
                        throw new SyntaxNodeException(modifier, "unsafe set twice");
                    }
                    this.@unsafe = true;
                }
                else if(text == "new")
                {
                    if (this.@new)
                    {
                        throw new SyntaxNodeException(modifier, "new set twice");
                    }
                    this.@new = true;
                }
                else if(text == "volatile")
                {
                    if(this.@volatile)
                    {
                        throw new SyntaxNodeException(modifier, "volatile set twice");
                    }
                    this.@volatile = true;
                }
                else if(text == "fixed")
                {
                    if(this.@fixed)
                    {
                        throw new SyntaxNodeException(modifier, "fixed set twice");
                    }
                    this.@fixed = true;
                }
                else
                {
                    throw new NotImplementedException(String.Format(
                        "Modifier '{0}' not implemented", text));
                }
            }

            if(dlangVisibility == null)
            {
                dlangVisibility = "private";
            }
        }
        void EnforceNoVisibilityYet(SyntaxToken modifierToken, String visibility)
        {
            if (this.csharpVisibility != null)
            {
                throw new SyntaxNodeException(modifierToken, String.Format("visibility set twice '{0}' and '{1}'",
                    this.csharpVisibility, visibility));
            }
        }
        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();

            bool atFirst = true;

            if(csharpVisibility != null)
            {
                if(atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append(csharpVisibility);
            }
            if(partial)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("partial");
            }
            if (@static)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("static");
            }
            if (@const)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("const");
            }
            if (@readonly)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("readonly");
            }
            if (@abstract)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("abstract");
            }
            if (@sealed)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("sealed");
            }
            if (@unsafe)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("unsafe");
            }
            if (@new)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("new");
            }
            if (@volatile)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("volatile");
            }
            if (@fixed)
            {
                if (atFirst) { atFirst = false; } else { builder.Append(' '); }
                builder.Append("fixed");
            }
            return builder.ToString();
        }
    }
}
