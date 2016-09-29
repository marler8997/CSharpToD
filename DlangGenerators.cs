using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
        Decl,
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
        public void AddAttributeLists(SyntaxList<AttributeListSyntax> attributeLists)
        {
            this.attributeLists.Add(attributeLists);
        }
        public void AddDecl(MemberDeclarationSyntax decl)
        {
            this.decls.Add(decl);
        }
    }

    public class DlangGenerator : IComparable<DlangGenerator>
    {
        public readonly ProjectModels projectModels;
        public readonly string csharpNamespace;
        public readonly string dlangModule;
        public BufferedNativeFileSink log;
        Boolean putInPackage;
        public string filenameFullPath;
        public int CompareTo(DlangGenerator other)
        {
            return filenameFullPath.CompareTo(other.filenameFullPath);
        }

        readonly Dictionary<CSharpFileModel, CSharpFileModelNodes> fileModelNodeMap =
            new Dictionary<CSharpFileModel, CSharpFileModelNodes>();
        //List<String> includeSourceFiles;

        Dictionary<string, object> typeNameMap = new Dictionary<string, object>();

        public DlangGenerator(ProjectModels projectModels, String csharpNamespace)
        {
            this.projectModels = projectModels;
            this.csharpNamespace = csharpNamespace;
            this.dlangModule = (csharpNamespace.Length == 0) ? projectModels.assemblyPackageName :
                String.Format("{0}.{1}", projectModels.assemblyPackageName, csharpNamespace);
        }
        public void SetPutInPackage(bool putInPackage)
        {
            this.putInPackage = putInPackage;
        }

        public String GetModuleName(String assembly, String @namespace)
        {
            throw new NotImplementedException();
        }

        readonly Dictionary<IAssemblySymbol, string> assemblyPackageNameMap = new
            Dictionary<IAssemblySymbol, string>();
        public string GetAssemblyPackageName(IAssemblySymbol assemblySymbol)
        {
            if(assemblySymbol == null)
            {
                throw new InvalidOperationException();
            }

            String packageName;
            if (!assemblyPackageNameMap.TryGetValue(assemblySymbol, out packageName))
            {
                if(assemblySymbol.Name == projectModels.project.AssemblyName)
                {
                    packageName = projectModels.assemblyPackageName;
                }
                else
                {
                    packageName = CSharpToD.GetAssemblyPackageName(assemblySymbol.Name);
                }
                assemblyPackageNameMap.Add(assemblySymbol, packageName);
            }
            return packageName;
        }

        readonly Dictionary<ITypeSymbol, string> moduleAndContainingTypeMap = new
            Dictionary<ITypeSymbol, string>();

        public String GetModuleAndContainingType(ITypeSymbol typeSymbol)
        {
            String moduleAndContainingType;
            if(!moduleAndContainingTypeMap.TryGetValue(typeSymbol, out moduleAndContainingType))
            {
                if(typeSymbol.ContainingType != null)
                {
                    moduleAndContainingType = String.Format("{0}.{1}.{2}",
                        GetModuleAndContainingType(typeSymbol.ContainingType),
                        typeSymbol.ContainingType.Name, typeSymbol.Name);
                }
                else
                {
                    String packageName = GetAssemblyPackageName(typeSymbol.ContainingAssembly);
                    if (typeSymbol.ContainingNamespace.Name.Length == 0)
                    {
                        moduleAndContainingType = packageName;
                    }
                    else
                    {
                        moduleAndContainingType = String.Format("{0}.{1}", packageName, typeSymbol.ContainingNamespace.ToString());
                    }
                }
                moduleAndContainingTypeMap.Add(typeSymbol, moduleAndContainingType);
            }
            return moduleAndContainingType;
        }
        /*
        public string DotNetToD(TypeContext context, INamedTypeSymbol typeSymbol)
        {
            String moduleAndContainingType = GetModuleAndContainingType(typeSymbol);
            return SemanticExtensions.DotNetToD(context, moduleAndContainingType, typeSymbol.Name, (uint)typeSymbol.Arity);
        }
        public string DotNetToD(TypeContext context, ITypeSymbol typeSymbol)
        {
            String moduleAndContainingType = GetModuleAndContainingType(typeSymbol);
            INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
            uint arity = (namedTypeSymbol == null) ? 0 : (uint)namedTypeSymbol.Arity;
            return SemanticExtensions.DotNetToD(context, moduleAndContainingType, typeSymbol.Name, arity);
        }
        */
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

        public void Finish()
        {
            try
            {
                if (CSharpToD.log)
                {
                    this.log = new BufferedNativeFileSink(NativeFile.Open(
                        Path.Combine(CSharpToD.logDirectory, dlangModule),
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
                    if (putInPackage)
                    {
                        filenameRelative = Path.Combine(
                            dlangModule.Replace('.', Path.DirectorySeparatorChar), "package.d");
                    }
                    else
                    {
                        int lastDotIndex = dlangModule.LastIndexOf('.');
                        if (lastDotIndex < 0)
                        {
                            filenameRelative = dlangModule + ".d";
                        }
                        else
                        {
                            filenameRelative = Path.Combine(
                                dlangModule.Remove(lastDotIndex).Replace('.', Path.DirectorySeparatorChar),
                                dlangModule.Substring(lastDotIndex + 1) + ".d");
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
                    writer.WriteLine("module {0};", dlangModule);
                    writer.WriteLine();

                    //
                    // First Pass: find imports
                    //
                    FirstPassVisitor firstPass = new FirstPassVisitor(this, writer, log);
                    foreach (CSharpFileModelNodes fileModelNodes in fileModelNodesArray)
                    {
                        foreach (MemberDeclarationSyntax decl in fileModelNodes.decls)
                        {
                            firstPass.currentFileModel = fileModelNodes.fileModel;
                            firstPass.Visit(decl);
                        }
                    }

                    /*
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
                    */

                    foreach (KeyValuePair<String, HashSet<TypeSymbolAndArity>> moduleAndTypes in firstPass.typeSymbolsByModule)
                    {
                        if (moduleAndTypes.Key != this.dlangModule)
                        {
                            if (moduleAndTypes.Key.Length == 0)
                            {
                                throw new InvalidOperationException();
                            }

                            writer.WriteLine("import {0} :", moduleAndTypes.Key);
                            writer.Tab();
                            uint typeIndex = 0;
                            uint lastIndex = (uint)moduleAndTypes.Value.Count - 1;
                            foreach (TypeSymbolAndArity typeSymbol in moduleAndTypes.Value)
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
                        visitor.currentFileModel = fileModelNodes.fileModel;
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

    class CSharpToDVisitor : CSharpSyntaxVisitor
    {
        protected readonly DlangGenerator generator;
        public CSharpToDVisitor(DlangGenerator generator)
        {
            this.generator = generator;
        }

        public CSharpFileModel currentFileModel;

        protected bool TypeIsRemoved(TypeDeclarationSyntax typeDecl)
        {
            if (generator.projectModels.config.typesToRemove.Count > 0)
            {
                INamedTypeSymbol typeSymbol = currentFileModel.semanticModel.GetDeclaredSymbol(typeDecl);
                if (typeSymbol.Arity == 0)
                {
                    String moduleAndContainingType = generator.GetModuleAndContainingType(typeSymbol);
                    DType dType = SemanticExtensions.DotNetToD(TypeContext.Decl,
                        moduleAndContainingType, typeSymbol.Name, (uint)typeSymbol.Arity);
                    int fullTypeDeclNameLength = moduleAndContainingType.Length + 1 + dType.name.Length;
                    foreach (String typeToRemove in generator.projectModels.config.typesToRemove)
                    {
                        if (typeToRemove.Length == fullTypeDeclNameLength &&
                            typeToRemove.StartsWith(moduleAndContainingType) &&
                            typeToRemove[moduleAndContainingType.Length] == '.' &&
                            typeToRemove.EndsWith(dType.name))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }

    class FirstPassVisitor : CSharpToDVisitor
    {
        readonly DlangWriter writer;
        readonly BufferedNativeFileSink log;
        
        readonly HashSet<ITypeSymbol> typesAlreadyAdded = new HashSet<ITypeSymbol>();
        public readonly KeyUniqueValues<string, TypeSymbolAndArity> typeSymbolsByModule =
            new KeyUniqueValues<string, TypeSymbolAndArity>(true);

        public readonly KeyValues<string, FileAndTypeDecl> partialTypes =
            new KeyValues<string, FileAndTypeDecl>(true);

        public FirstPassVisitor(DlangGenerator generator, DlangWriter writer, BufferedNativeFileSink log)
            : base(generator)
        {
            this.writer = writer;
            this.log = log;
        }

        public override void DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException(String.Format("FirstPassVisitor for '{0}'", node.GetType().Name));
        }
        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            foreach (var member in node.Members)
            {
                Visit(member);
            }
        }
        
        void AddNewType(String module, String dlangTypeIdentifier, UInt32 arity)
        {
            //String dModule = SemanticExtensions.NamespaceToDModule(@namespace);
            typeSymbolsByModule.Add(module, new TypeSymbolAndArity(dlangTypeIdentifier, arity));
            //Console.WriteLine("Namespace '{0}', from symbol '{1}'", typeSymbol.ContainingModule(), typeSymbol.Name);
            //writer.WriteCommentedLine(String.Format("Namespace '{0}', from symbol '{1}'", typeSymbol.ContainingModule(), typeSymbol.Name));
            if (log != null)
            {
                log.PutLine(String.Format("Module '{0}', from symbol '{1}'", module, dlangTypeIdentifier));
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

                        String module = generator.GetModuleAndContainingType(typeSymbol);
                        DType dType = SemanticExtensions.DotNetToD(TypeContext.Default,
                            module, typeSymbol.Name, arity);
                        if (!dType.isPrimitive)
                        {
                            AddNewType(module, dType.name, arity);
                        }

                        if (log != null)
                        {
                            log.PutLine(String.Format("Adding generic types from {0}.{1}",
                                module, typeSymbol.Name));
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
            if(TypeIsRemoved(typeDecl))
            {
                return;
            }

            if (typeDecl.Modifiers.ContainsPartial())
            {
                partialTypes.Add(typeDecl.Identifier.Text,
                    new FileAndTypeDecl(currentFileModel, typeDecl));
            }
            if(typeDecl.BaseList == null)
            {
                if (typeDeclType == TypeDeclType.Class)
                {
                    AddNewType("mscorlib.System", "DotNetObject", 0);
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
                        AddNewType("mscorlib.System", "DotNetObject", 0);
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
                    AddNewType("mscorlib.System", "DotNetObject", 0);
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
        public override void VisitEnumDeclaration(EnumDeclarationSyntax enumDecl)
        {
            foreach (EnumMemberDeclarationSyntax enumMember in enumDecl.Members)
            {
                if (enumMember.EqualsValue != null)
                {
                    if (!CSharpToD.skeleton)
                    {
                        Visit(enumMember.EqualsValue.Value);
                    }
                }
            }
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax fieldDecl)
        {
            ITypeSymbol fieldType = currentFileModel.semanticModel.GetTypeInfo(fieldDecl.Declaration.Type).Type;
            AddNamespaceFrom(fieldType);
        }
        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            // TODO: implement this
        }
        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            // TODO: implement this
        }
        public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node)
        {
            // TODO: implement this
        }
        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // TODO: implement this
        }
        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            // TODO: implement this
        }
        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            // TODO: implement this
        }
        public override void VisitEventDeclaration(EventDeclarationSyntax node)
        {
            // TODO: implement this
        }
        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            // TODO: implement this
        }
        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            // TODO: implement this
        }


        //
        // Expressions
        //
        public override void VisitParenthesizedExpression(ParenthesizedExpressionSyntax parensExpression)
        {
            Visit(parensExpression.Expression);
        }
        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            throw new NotImplementedException("VisitAssignmentExpression");
        }
        public override void VisitLiteralExpression(LiteralExpressionSyntax literalExpression)
        {
            // I think literals do not contain types...not 100% sure though
        }
        public override void VisitBinaryExpression(BinaryExpressionSyntax binaryExpression)
        {
            Visit(binaryExpression.Left);
            Visit(binaryExpression.Right);
        }
        public override void VisitIdentifierName(IdentifierNameSyntax identifier)
        {
            TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(identifier);
            if(typeInfo.Type != null)
            {
                // TODO: I will need the type namespace, but maybe not the type symbol?
                AddNamespaceFrom(typeInfo.Type);
            }
        }
        public override void VisitCastExpression(CastExpressionSyntax cast)
        {
            AddNamespaceFrom(currentFileModel.semanticModel.GetTypeInfo(cast.Type).Type);
            Visit(cast.Expression);
        }
        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax memberAccess)
        {
            Visit(memberAccess.Expression);
            TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(memberAccess.Name);
            if(typeInfo.Type != null)
            {
                // TODO: I will need the type namespace, but maybe not the type symbol?
                AddNamespaceFrom(typeInfo.Type);
            }
        }
        public override void VisitCheckedExpression(CheckedExpressionSyntax checkedExpression)
        {
            Visit(checkedExpression.Expression);
        }
        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax unaryExpression)
        {
            Visit(unaryExpression.Operand);
        }
        public override void VisitEqualsValueClause(EqualsValueClauseSyntax equalsValueClause)
        {
            Visit(equalsValueClause.Value);
        }
        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            throw new NotImplementedException();
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
    class DlangVisitorGenerator : CSharpToDVisitor
    {
        public readonly DlangWriter writer;

        readonly FirstPassVisitor firstPass;

        bool insideNamespace;
        readonly Stack<DeclContext> currentDeclContext =
            new Stack<DeclContext>();

        public DlangVisitorGenerator(DlangGenerator generator, DlangWriter writer,
            FirstPassVisitor firstPass) : base(generator)
        {
            this.writer = writer;
            this.firstPass = firstPass;
        }
        public override void DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException(String.Format("DlangVisitorGenerator for {0}", node.GetType().Name));
        }
        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax namespaceDecl)
        {
            String namespaceName = namespaceDecl.Name.GetIdentifierUsingVisitor();
            if (insideNamespace)
            {
                writer.WriteCommentedLine(String.Format("Nested namespace '{0}' has been moved", namespaceName));
            }
            else
            {
                if (namespaceDecl.Externs.HasItems())
                {
                    throw new NotImplementedException();
                }
                try
                {
                    insideNamespace = true;
                    foreach (var member in namespaceDecl.Members)
                    {
                        Visit(member);
                    }
                }
                finally
                {
                    insideNamespace = false;
                }
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
            if (TypeIsRemoved(typeDecl))
            {
                writer.WriteCommentedLine(String.Format(
                    "Type '{0}' was removed by configuration", typeDecl.Identifier.Text));
                return;
            }

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
            //       come up with a regression test for this case before implementing
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
                        WriteDlangType(TypeContext.Default, typeSymbol);
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
            INamedTypeSymbol typeSymbol = currentFileModel.semanticModel.GetDeclaredSymbol(typeDecl);
            String moduleAndContainingType = generator.GetModuleAndContainingType(typeSymbol);
            DType dType = SemanticExtensions.DotNetToD(TypeContext.Decl,
                moduleAndContainingType, typeSymbol.Name, (uint)typeSymbol.Arity);
            writer.Write(dType.name);
            if(typeSymbol.Arity > 0)
            {
                writer.Write("{0}", typeSymbol.Arity);
            }
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
                    WriteDlangType(TypeContext.Default, namedType);
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
            WriteDlangType(TypeContext.Return, typeInfo.Type);
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
            WriteDlangType(TypeContext.Default, typeSymbol);

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
                        if (CSharpToD.skeleton)
                        {
                            writer.WriteCommentedInline("[= ommited in 'skeleton' mode]");
                        }
                        else
                        {
                            writer.Write(" = ");
                            Visit(enumMember.EqualsValue.Value);
                        }
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
            if(modifiers.@static)
            {
                writer.Write("static ");
            }
            if(modifiers.@const)
            {
                writer.Write("enum "); // TODO: not sure if this is equivalent
            }
            if(modifiers.@readonly)
            {
                writer.Write("immutable "); // TODO: not sure if this is equivalent
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
            WriteDlangType(TypeContext.Default, typeSymbol);
            writer.Write(" ");

            bool atFirst = true;
            foreach (VariableDeclaratorSyntax variableDecl in fieldDecl.Declaration.Variables)
            {
                if (atFirst) { atFirst = false; } else { writer.Write(", "); }

                {
                    String identifierName = GetIdentifierName(variableDecl.Identifier.Text);
                    if(typeSymbol.DlangTypeStringEqualsIdentifier(identifierName))
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
                    if (CSharpToD.skeleton)
                    {
                        if (modifiers.@const)
                        {
                            // need to write a value
                            writer.Write(" = ");
                            WriteDlangDefaultValue(typeSymbol);
                        }
                        else
                        {
                            writer.WriteCommentedInline("initializer stripped in skeleton mode");
                        }
                    }
                    else
                    {
                        writer.WriteCommentedInline("todo: implement initializer");
                        writer.Write(" = ");
                        WriteDlangDefaultValue(typeSymbol);
                        //Visit(variableDecl.Initializer);
                    }
                }
            }
            writer.WriteLine(";");
        }

        public void WriteDlangTypeDeclName(String dlangModule, String identifier, UInt32 genericTypeCount)
        {
            DType dType = SemanticExtensions.DotNetToD(TypeContext.Default, dlangModule, identifier, genericTypeCount);
            writer.Write(dType.name);
            if (genericTypeCount > 0)
            {
                writer.Write("{0}", genericTypeCount);
            }
        }
        public void WriteDlangTypeDeclName(String @namespace, TypeDeclarationSyntax typeDecl)
        {
            UInt32 genericTypeCount = 0;
            if (typeDecl.TypeParameterList != null)
            {
                genericTypeCount = (uint)typeDecl.TypeParameterList.Parameters.Count;
            }
            WriteDlangTypeDeclName(@namespace, typeDecl.Identifier.ToString(), genericTypeCount);
        }
        public void WriteDlangDefaultValue(ITypeSymbol typeSymbol)
        {
            switch(typeSymbol.TypeKind)
            {
                case TypeKind.Array:
                case TypeKind.Class:
                case TypeKind.Delegate:
                case TypeKind.Interface:
                case TypeKind.Pointer:
                    writer.Write("null");
                    break;
                case TypeKind.Enum:
                    writer.Write("(cast(");
                    WriteDlangType(TypeContext.Default, typeSymbol);
                    writer.Write(")0)");
                    break;
                case TypeKind.Struct:
                    WriteDlangType(TypeContext.Default, typeSymbol);
                    writer.Write("()");
                    break;
                case TypeKind.TypeParameter:
                    throw new NotImplementedException();
                    break;
                default:
                    throw new NotImplementedException(String.Format("WriteDlangDefaultValue (type kind '{0}')", typeSymbol.TypeKind));
            }
        }
        public void WriteDlangType(TypeContext context, ITypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind == TypeKind.Array)
            {
                WriteDlangType(context, ((IArrayTypeSymbol)typeSymbol).ElementType);
                writer.Write("[]");
            }
            else if (typeSymbol.TypeKind == TypeKind.Pointer)
            {
                WriteDlangType(context, ((IPointerTypeSymbol)typeSymbol).PointedAtType);
                writer.Write("*");
            }
            else
            {
                INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
                int arity = (namedTypeSymbol == null) ? 0 : namedTypeSymbol.Arity;

                if (typeSymbol.Kind != SymbolKind.TypeParameter)
                {
                    if (typeSymbol.ContainingType != null)
                    {
                        if (!currentDeclContext.Inside(typeSymbol.ContainingType))
                        {
                            WriteDlangType(context, typeSymbol.ContainingType);
                            writer.Write(".");
                        }
                    }
                }

                string moduleAndContainingType = generator.GetModuleAndContainingType(typeSymbol);
                DType dType = SemanticExtensions.DotNetToD(context,
                    moduleAndContainingType, typeSymbol.Name, (uint)arity);
                writer.Write(dType.name);
                if (arity > 0)
                {
                    writer.Write("{0}!(", arity);
                    bool atFirst = true;
                    foreach (ITypeSymbol genericTypeArg in namedTypeSymbol.TypeArguments)
                    {
                        if (atFirst) { atFirst = false; } else { writer.Write(","); }
                        WriteDlangType(context, genericTypeArg);
                    }
                    writer.Write(")");
                }
            }
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

        //
        // Expressions
        //
        public override void VisitParenthesizedExpression(ParenthesizedExpressionSyntax parensExpression)
        {
            writer.Write("(");
            Visit(parensExpression.Expression);
            writer.Write(")");
        }
        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            throw new NotImplementedException("VisitAssignmentExpression");
        }
        public override void VisitLiteralExpression(LiteralExpressionSyntax literalExpression)
        {
            if(CSharpToD.generateDebug)
            {
                writer.Write("/*LiteralExpression(*/{0}/*)*/", literalExpression.Token.Text);
            }
            else
            {
                writer.Write(literalExpression.Token.Text);
            }
        }
        public override void VisitBinaryExpression(BinaryExpressionSyntax binaryExpression)
        {
            Visit(binaryExpression.Left);
            writer.Write(" ");
            writer.Write(binaryExpression.OperatorToken.Text);
            writer.Write(" ");
            Visit(binaryExpression.Right);
        }
        public override void VisitIdentifierName(IdentifierNameSyntax identifier)
        {
            if(CSharpToD.generateDebug)
            {
                writer.Write("/*IdentifierName(*/{0}/*)*/", identifier.Identifier.Text);
            }
            else
            {
                writer.Write(identifier.Identifier.Text);
            }
        }
        public override void VisitCastExpression(CastExpressionSyntax cast)
        {
            ITypeSymbol typeSymbol = currentFileModel.semanticModel.GetTypeInfo(cast.Type).Type;

            writer.Write("cast(");
            WriteDlangType(TypeContext.Default, typeSymbol);
            writer.Write(")");
            Visit(cast.Expression);
        }
        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax memberAccess)
        {
            if (CSharpToD.generateDebug)
            {
                writer.Write("/*MemberAccessExpression(*/");
            }
            Visit(memberAccess.Expression);
            writer.Write(memberAccess.OperatorToken.Text);
            writer.Write(memberAccess.Name.Identifier.Text);
            if (CSharpToD.generateDebug)
            {
                writer.Write("/*)*/");
            }
        }
        public override void VisitCheckedExpression(CheckedExpressionSyntax checkedExpression)
        {
            String operatorString = checkedExpression.Keyword.Text;
            if (operatorString == "unchecked")
            {
                // I think dlang is unchecked by default
                Visit(checkedExpression.Expression);
            }
            else
            {
                throw new NotImplementedException(String.Format("CheckedExpression '{0}'", operatorString));
            }
        }
        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax unaryExpression)
        {
            writer.Write(unaryExpression.OperatorToken.Text);
            Visit(unaryExpression.Operand);
        }
        public override void VisitEqualsValueClause(EqualsValueClauseSyntax equalsValueClause)
        {
            if(equalsValueClause.EqualsToken.Text != "=")
            {
                throw new NotImplementedException();
            }
            writer.Write(" = ");
            Visit(equalsValueClause.Value);
        }
        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            throw new NotImplementedException();
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

    public struct DType
    {
        public readonly Boolean isPrimitive;
        public readonly String name;
        public DType(Boolean isPrimitive, String name)
        {
            this.isPrimitive = isPrimitive;
            this.name = name;
        }
    }

    public static class SemanticExtensions
    {
        static readonly Dictionary<string, DType> PrimitiveSystemDeclTypeMap = new Dictionary<string, DType>
        {
            { "Object", new DType(false, "DotNetObject") },
            {"Exception", new DType(false, "DotNetException") },
        };
        static readonly Dictionary<string, DType> PrimitiveSystemTypeMap = new Dictionary<string, DType>
        {
            {"Boolean", new DType(true, "bool") },
            {"Char", new DType(true, "wchar") },

            {"Byte", new DType(true, "ubyte") },
            {"SByte", new DType(true, "byte") },

            {"Int16", new DType(true, "short") },
            {"UInt16", new DType(true, "ushort") },

            {"Int32", new DType(true, "int") },
            {"UInt32", new DType(true, "uint") },

            {"Int64", new DType(true, "long") },
            {"UInt64", new DType(true, "ulong") },

            {"Single", new DType(true, "float") },
            {"Double", new DType(true, "double") },

            { "Object", new DType(false, "DotNetObject") },
            {"Exception", new DType(false, "DotNetException") },
        };
        static readonly Dictionary<string, DType> PrimitiveSystemReflectionTypeMap = new Dictionary<string, DType>
        {
            {"TypeInfo", new DType(false, "DotNetTypeInfo") },
        };
        public static DType DotNetToD(TypeContext context, string moduleAndContainingType, string typeName, UInt32 genericTypeCount)
        {
            if (genericTypeCount == 0)
            {
                if (moduleAndContainingType == "mscorlib.System")
                {
                    if (context == TypeContext.Return && typeName == "Void")
                    {
                        return new DType(true, "void");
                    }

                    DType dlangType;
                    if (context == TypeContext.Decl)
                    {
                        if (PrimitiveSystemDeclTypeMap.TryGetValue(typeName, out dlangType))
                        {
                            return dlangType;
                        }
                    }
                    else // Default/Return context
                    {
                        if (PrimitiveSystemTypeMap.TryGetValue(typeName, out dlangType))
                        {
                            return dlangType;
                        }
                    }
                }
                else if (moduleAndContainingType == "mscorlib.System.Reflection")
                {
                    DType dlangType;
                    if (PrimitiveSystemReflectionTypeMap.TryGetValue(typeName, out dlangType))
                    {
                        return dlangType;
                    }
                }
            }
            return new DType(false, typeName);
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
        public static Boolean DlangTypeStringEqualsIdentifier(this ITypeSymbol typeSymbol, String identifier)
        {
            if (typeSymbol.TypeKind == TypeKind.Array)
            {
                return false;
            }
            else if (typeSymbol.TypeKind == TypeKind.Pointer)
            {
                return false;
            }
            else
            {
                INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
                if (namedTypeSymbol != null && namedTypeSymbol.Arity > 0)
                {
                    return false;
                }

                return typeSymbol.Name.Equals(identifier);
            }
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
                        this.dlangVisibility = "public"; // treat 'internal' as 'public'
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
