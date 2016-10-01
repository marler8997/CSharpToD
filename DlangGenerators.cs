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
        Default, // Write the type using the default context
        Decl, // Write the type as if it is a declaration
        Return, // Write the type as if it is a return type
        AfterDot, // Write the type as if the parent namespace/type has already been written
    }

    public class CSharpFileModelNodes : IComparable<CSharpFileModelNodes>
    {
        public readonly CSharpFileModel fileModel;
        public readonly List<SyntaxNode> syntaxNodes = new List<SyntaxNode>();

        public List<AttributeSyntax> assemblyAttributes = null;
        public List<AttributeSyntax> moduleAttributes = null;

        public CSharpFileModelNodes(CSharpFileModel fileModel)
        {
            this.fileModel = fileModel;
        }
        public int CompareTo(CSharpFileModelNodes other)
        {
            return fileModel.document.FilePath.CompareTo(other.fileModel.document.FilePath);
        }
        public void Add(SyntaxNode node)
        {
            this.syntaxNodes.Add(node);
        }

        public static void AddAssemblyAttribute(CSharpFileModelNodes fileModelNodes, AttributeSyntax attr)
        {
            if(fileModelNodes.assemblyAttributes == null)
            {
                fileModelNodes.assemblyAttributes = new List<AttributeSyntax>();
            }
            fileModelNodes.assemblyAttributes.Add(attr);
        }
        public static void AddModuleAttribute(CSharpFileModelNodes fileModelNodes, AttributeSyntax attr)
        {
            if (fileModelNodes.moduleAttributes == null)
            {
                fileModelNodes.moduleAttributes = new List<AttributeSyntax>();
            }
            fileModelNodes.moduleAttributes.Add(attr);
        }

        public static List<AttributeSyntax> GetAssemblyAttributes(CSharpFileModelNodes fileModelNodes)
        {
            return fileModelNodes.assemblyAttributes;
        }
        public static List<AttributeSyntax> GetModuleAttributes(CSharpFileModelNodes fileModelNodes)
        {
            return fileModelNodes.moduleAttributes;
        }
    }
    public delegate void AttributeAdder(CSharpFileModelNodes fileModel, AttributeSyntax attr);
    public delegate List<AttributeSyntax> AttributeGetter(CSharpFileModelNodes fileModel);

    public class DlangGenerator : IComparable<DlangGenerator>
    {
        public readonly ProjectModels projectModels;
        public readonly string csharpNamespace;
        public readonly string dlangModule;
        Boolean putInPackage;
        public string filenameFullPath;
        public int CompareTo(DlangGenerator other)
        {
            return filenameFullPath.CompareTo(other.filenameFullPath);
        }

        readonly Dictionary<CSharpFileModel, CSharpFileModelNodes> fileModelNodeMap =
            new Dictionary<CSharpFileModel, CSharpFileModelNodes>();
        List<String> includeSourceFiles;

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
                    int arity = typeSymbol.ContainingType.Arity;
                    if (arity == 0)
                    {
                        moduleAndContainingType = String.Format("{0}.{1}",
                            GetModuleAndContainingType(typeSymbol.ContainingType),
                            typeSymbol.ContainingType.Name);
                    }
                    else
                    {
                        moduleAndContainingType = String.Format("{0}.{1}{2}",
                            GetModuleAndContainingType(typeSymbol.ContainingType),
                            typeSymbol.ContainingType.Name, arity);
                    }
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
        public void Add(CSharpFileModel fileModel, SyntaxNode node)
        {
            GetFileModelNodes(fileModel).Add(node);
        }
        public void AddIncludeSource(String includeSourceFile)
        {
            if(includeSourceFiles == null)
            {
                includeSourceFiles = new List<string>();
            }
            includeSourceFiles.Add(includeSourceFile);
        }

        public void Finish()
        {
            try
            {
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
                    FirstPassVisitor firstPass = new FirstPassVisitor(this, writer);
                    if (CSharpToD.generateDebug)
                    {
                        writer.WriteLine("/*");
                        writer.WriteLine("================================================================================");
                        writer.WriteLine("============================= Start First Pass Debug ===========================");
                        writer.WriteLine("================================================================================");
                    }
                    foreach (CSharpFileModelNodes fileModelNodes in fileModelNodesArray)
                    {
                        if (CSharpToD.generateDebug)
                        {
                            writer.WriteLine();
                            writer.WriteLine("--------------------------------------------------------------------------------");
                            writer.WriteLine(" File: {0}", fileModelNodes.fileModel.document.FilePath);
                            writer.WriteLine("--------------------------------------------------------------------------------");
                        }
                        foreach (SyntaxNode decl in fileModelNodes.syntaxNodes)
                        {
                            firstPass.SetFileContext(fileModelNodes);
                            firstPass.Visit(decl);
                        }
                    }
                    if (CSharpToD.generateDebug)
                    {
                        writer.WriteLine("================================================================================");
                        writer.WriteLine("============================= End First Pass Debug =============================");
                        writer.WriteLine("================================================================================");
                        writer.WriteLine("*/");
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
                                    csharpNamespace, includeSourceFullPath));
                            }
                            writer.WriteLine();
                            writer.WriteLine("//");
                            writer.WriteLine("// Source Included from '{0}'", includeSourceFullPath);
                            writer.WriteLine("//");
                            using (FileStream stream = new FileStream(includeSourceFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                writer.Write(stream);
                            }
                        }
                    }

                    if (CSharpToD.generateDebug)
                    {
                        foreach (KeyValuePair<TypeSymbolAndArity, HashSet<string>> pair in firstPass.modulesByTypeName)
                        {
                            TypeSymbolAndArity type = pair.Key;
                            HashSet<string> modules = pair.Value;

                            if (modules.Count == 1)
                            {
                                var enumerator = modules.GetEnumerator();
                                enumerator.MoveNext();
                                var module = enumerator.Current;
                                if (type.arity == 0)
                                {
                                    writer.WriteLine("// Type '{0}' -> '{1}'", type.name, module);
                                }
                                else
                                {
                                    writer.WriteLine("// Type '{0}{1}' -> '{2}'", type.name, type.arity, module);
                                }
                            }
                            else
                            {
                                if (type.arity == 0)
                                {
                                    writer.WriteLine("// Type '{0}' found in {1} modules", type.name, modules.Count);
                                }
                                else
                                {
                                    writer.WriteLine("// Type '{0}{1}' found in {2} modules", type.name, type.arity, modules.Count);
                                }
                                foreach (string module in modules)
                                {
                                    writer.WriteLine("//     -> '{0}'", module);
                                }
                            }
                        }
                    }

                    foreach (KeyValuePair<String, HashSet<TypeSymbolAndArity>> moduleAndTypes in firstPass.importTypesByModule)
                    {
                        if (moduleAndTypes.Key != this.dlangModule)
                        {
                            if (moduleAndTypes.Key.Length == 0)
                            {
                                throw new InvalidOperationException();
                            }

                            bool printedImport = false;
                            bool nameConflict = false;
                            foreach (TypeSymbolAndArity typeSymbol in moduleAndTypes.Value)
                            {
                                HashSet<string> typesWithSameName =
                                    firstPass.modulesByTypeName[typeSymbol];
                                if (typesWithSameName.Count == 1)
                                {
                                    if (printedImport)
                                    {
                                        writer.WriteLine(",");
                                    }
                                    else
                                    {
                                        if (nameConflict)
                                        {
                                            writer.WriteLine();
                                        }
                                        writer.WriteLine("import {0} :", moduleAndTypes.Key);
                                        writer.Tab();
                                        printedImport = true;
                                    }

                                    writer.Write(typeSymbol.name);
                                    if (typeSymbol.arity > 0)
                                    {
                                        writer.Write("{0}", typeSymbol.arity);
                                    }
                                }
                                else
                                {
                                    if (!printedImport)
                                    {
                                        if (nameConflict)
                                        {
                                            writer.WriteLine();
                                        }
                                    }
                                    nameConflict = true;
                                    if (typeSymbol.arity == 0)
                                    {
                                        writer.Write("/* NameConflict: {0}*/", typeSymbol.name);
                                    }
                                    else
                                    {
                                        writer.Write("/* NameConflict: {0}{1}*/", typeSymbol.name, typeSymbol.arity);
                                    }
                                }
                            }
                            if (printedImport)
                            {
                                writer.WriteLine(";");
                                writer.Untab();
                            }
                            if (nameConflict)
                            {
                                writer.WriteLine("static import {0};", moduleAndTypes.Key);
                            }
                        }
                    }

                    {
                        DlangVisitorGenerator visitor = new DlangVisitorGenerator(this, writer, firstPass);

                        //
                        // Write Assembly and Module Attributes
                        //
                        {
                            String target = "assembly";
                            AttributeGetter attributeGetter = CSharpFileModelNodes.GetAssemblyAttributes;
                            while (true)
                            {
                                bool startedArray = false;
                                foreach (CSharpFileModelNodes fileModelNodes in fileModelNodesArray)
                                {
                                    visitor.SetFileContext(fileModelNodes);
                                    List<AttributeSyntax> attributes = attributeGetter(fileModelNodes);
                                    if (attributes != null)
                                    {
                                        if (!startedArray)
                                        {
                                            writer.WriteLine();
                                            writer.WriteLine("immutable __DotNet__AttributeStruct[] {0}Attributes = [", target);
                                            writer.Tab();
                                            startedArray = true;
                                        }
                                        foreach (AttributeSyntax attribute in attributes)
                                        {
                                            ITypeSymbol attributeType = fileModelNodes.fileModel.semanticModel.GetTypeInfo(attribute).Type;
                                            if (attributeType == null) throw new InvalidOperationException();
                                            visitor.WriteAttributeConstruction(target, attribute, attributeType, attribute.ArgumentList);
                                            writer.WriteLine(",");
                                        }
                                    }
                                }

                                if (startedArray)
                                {
                                    writer.Untab();
                                    writer.WriteLine("];");
                                    if (csharpNamespace.Length > 0)
                                    {
                                        throw new InvalidOperationException(String.Format(
                                            "CSharpError? found a(n) {0} attribute inside a namespace", target));
                                    }
                                }

                                if (attributeGetter == CSharpFileModelNodes.GetAssemblyAttributes)
                                {
                                    attributeGetter = CSharpFileModelNodes.GetModuleAttributes;
                                    target = "module";
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }

                        //
                        // Generate Code for each file
                        //
                        foreach (CSharpFileModelNodes fileModelNodes in fileModelNodesArray)
                        {
                            visitor.SetFileContext(fileModelNodes);
                            writer.WriteLine();
                            writer.WriteLine("//");
                            writer.WriteLine("// Source Generated From '{0}'", fileModelNodes.fileModel.document.FilePath);
                            writer.WriteLine("//");
                            foreach (SyntaxNode node in fileModelNodes.syntaxNodes)
                            {
                                visitor.Visit(node);
                            }
                        }
                    }
                }
            }
            finally
            {
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
        public override int GetHashCode()
        {
            return name.GetHashCode() + (int)arity;
        }
        public override string ToString()
        {
            return name + arity.ToString();
        }
    }
    enum TypeDeclType
    {
        Class = 0,
        Interface = 1,
        Struct = 2,
        Enum = 3,
    }

    class CSharpToDVisitor : CSharpSyntaxVisitor
    {
        protected readonly DlangGenerator generator;
        public CSharpToDVisitor(DlangGenerator generator)
        {
            this.generator = generator;
        }

        protected CSharpFileModel currentFileModel;
        protected CSharpFileModelNodes currentFileModelNodes;
        public void SetFileContext(CSharpFileModelNodes fileModelNodes)
        {
            this.currentFileModel = fileModelNodes.fileModel;
            this.currentFileModelNodes = fileModelNodes;
        }
        public void SetFileContext(CSharpFileModel fileModel)
        {
            this.currentFileModel = fileModel;
            this.currentFileModelNodes = null;
        }


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


        void WriteLeadingComments(SyntaxNode node, bool inline)
        {
            if (inline)
            {
                writer.Write("/*todo: inline leading comments*/");
            }
            else
            {
                writer.WriteCommented(currentFileModel.syntaxTree.GetText().GetSubText(node.GetLeadingTrivia().Span));
            }
        }

        static readonly Dictionary<string, string> AttributeTargetMap = new Dictionary<string, string>
        {
            {"assembly", "assembly" },
            {"module", "module_" },
        };
        public void WriteAttributeConstruction(String target, SyntaxNode location,
            ITypeSymbol attributeType, AttributeArgumentListSyntax arguments)
        {
            writer.Write("__DotNet__Attribute!(");
            if (target != null)
            {
                writer.Write("__DotNet__AttributeStruct.Target.{0}, ", AttributeTargetMap[target]);
            }
            WriteDlangType(location, TypeContext.Default, attributeType);
            writer.Write(".stringof");
            if (arguments != null)
            {
                writer.Write("/*");
                foreach (AttributeArgumentSyntax argument in arguments.Arguments)
                {
                    writer.Write(", {0}", argument.GetText().ToString().Trim().Replace("/*", "").Replace("*/", ""));
                }
                writer.Write("*/");
            }
            writer.Write(")");
        }

        void WriteAttribute(AttributeTargetSpecifierSyntax target, AttributeSyntax attribute, Boolean inline)
        {
            ITypeSymbol attributeType = currentFileModel.semanticModel.GetTypeInfo(attribute).Type;
            if (attributeType == null) throw new InvalidOperationException();

            String targetString = (target == null) ? null : target.Identifier.Text;

            if (targetString == "assembly" || targetString == "module")
            {
                // These attributes will be moved to the no namespace package
            }
            else
            {
                writer.Write("@");
                WriteAttributeConstruction(targetString, attribute, attributeType, attribute.ArgumentList);
                if (!inline)
                {
                    writer.WriteLine();
                }
            }
        }
        public void WriteAttributes(AttributeListSyntax attributeList, Boolean inline)
        {
            WriteLeadingComments(attributeList, inline);
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                WriteAttribute(attributeList.Target, attribute, inline);
            }
        }
        public void WriteAttributeLists(SyntaxList<AttributeListSyntax> attributeLists, Boolean inline)
        {
            foreach (AttributeListSyntax attributeList in attributeLists)
            {
                WriteAttributes(attributeList, inline);
            }
        }

        public override void VisitAttributeList(AttributeListSyntax attributeList)
        {
            WriteAttributes(attributeList, false);
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
                    writer.WriteLine("// partial class '{0}' moved", typeDecl.Identifier.Text);
                    return;
                }
                typeSymbol = partialTypeDecls[0].fileModel.semanticModel.GetDeclaredSymbol(partialTypeDecls[0].typeDecl);
                foreach (FileAndTypeDecl partialTypeDecl in partialTypeDecls)
                {
                    CSharpFileModel saveModel = currentFileModel;
                    try
                    {
                        SetFileContext(partialTypeDecl.fileModel);
                        WriteAttributeLists(partialTypeDecl.typeDecl.AttributeLists, false);
                    }
                    finally
                    {
                        SetFileContext(saveModel);
                    }
                }
            }
            else
            {
                typeSymbol = currentFileModel.semanticModel.GetDeclaredSymbol(typeDecl);
                partialTypeDecls = null;
                WriteAttributeLists(typeDecl.AttributeLists, false);
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
                default:
                    throw new InvalidOperationException("CodeBug");
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
                    if (currentDeclContext.Count > 0)
                    {
                        writer.Write("static ");
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
                        WriteDlangType(typeDecl, TypeContext.Default, typeSymbol);
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
                    writer.Write(" : __DotNet__Object");
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
                        writer.Write("__DotNet__Object");
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
                    WriteDlangType(baseList, TypeContext.Default, namedType);
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
            WriteAttributeLists(delegateDecl.AttributeLists, false);

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
            WriteDlangType(delegateDecl.ReturnType, TypeContext.Return, typeInfo.Type);
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
            if (param.AttributeLists.Count > 0)
            {
                writer.Write("/*todo: param attributes*/");
                //WriteAttributeLists(param.AttributeLists, true);
            }
            ParamModifiers modifiers = new ParamModifiers(param.Modifiers);

            if(modifiers.refout != null)
            {
                writer.Write(modifiers.refout);
                writer.Write(" ");
            }

            ITypeSymbol typeSymbol = currentFileModel.semanticModel.GetTypeInfo(param.Type).Type;
            WriteDlangType(param.Type, TypeContext.Default, typeSymbol);

            writer.Write(" ");
            writer.Write(param.Identifier.Text);
            if(param.Default != null)
            {
                throw new NotImplementedException();
            }
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax enumDecl)
        {
            WriteAttributeLists(enumDecl.AttributeLists, false);

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
                WriteBaseList(TypeDeclType.Enum, enumDecl.BaseList);
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
                    {
                        WriteLeadingComments(enumMember, false);
                        // Enums cannot have attributes in D
                        //WriteAttributeLists(enumMember.AttributeLists, false);
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

        void WriteFieldType(FieldDeclarationSyntax fieldDecl, ModifierCategories modifiers, ITypeSymbol typeSymbol)
        {

            if (modifiers.partial || modifiers.@abstract || modifiers.@sealed)
            {
                throw new SyntaxNodeException(fieldDecl, "Invalid modifier for field");
            }

            if (modifiers.dlangVisibility != null)
            {
                writer.Write(modifiers.dlangVisibility);
                writer.Write(" ");
            }
            if (modifiers.@static)
            {
                writer.Write("static ");
            }
            if (modifiers.@const)
            {
                writer.Write("enum "); // TODO: not sure if this is equivalent
            }
            if (modifiers.@readonly)
            {
                writer.Write("immutable "); // TODO: not sure if this is equivalent
            }
            if (modifiers.@new)
            {
                writer.Write("/*todo: new modifier*/ ");
            }
            if (modifiers.@volatile)
            {
                writer.Write("/*todo: volatile*/ ");
            }
            if (modifiers.@fixed)
            {
                writer.Write("/*todo: fixed*/ ");
            }
            WriteDlangType(fieldDecl.Declaration.Type, TypeContext.Default, typeSymbol);
        }
        void WriteFieldIdentifier(FieldDeclarationSyntax fieldDecl, ModifierCategories modifiers, ITypeSymbol typeSymbol, VariableDeclaratorSyntax variableDecl)
        {
            String identifierName = GetIdentifierName(variableDecl.Identifier.Text);
            if (typeSymbol.DlangTypeStringEqualsIdentifier(identifierName))
            {
                identifierName = identifierName + "_";
            }
            writer.Write(identifierName);

            if (variableDecl.Initializer != null)
            {
                if (CSharpToD.skeleton)
                {
                    if (modifiers.@const)
                    {
                        // need to write a value
                        writer.Write(" = ");
                        WriteDlangDefaultValue(fieldDecl.Declaration.Type, typeSymbol);
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
                    WriteDlangDefaultValue(fieldDecl.Declaration.Type, typeSymbol);
                    //Visit(variableDecl.Initializer);
                }
            }
        }
        public override void VisitFieldDeclaration(FieldDeclarationSyntax fieldDecl)
        {
            ModifierCategories modifiers = new ModifierCategories(fieldDecl.Modifiers);
            ITypeSymbol typeSymbol = currentFileModel.semanticModel.GetTypeInfo(fieldDecl.Declaration.Type).Type;

            //
            // Print non-array variables
            //
            {
                bool atFirst = true;
                foreach (VariableDeclaratorSyntax variableDecl in fieldDecl.Declaration.Variables)
                {
                    if (variableDecl.ArgumentList == null)
                    {
                        if (atFirst)
                        {
                            WriteAttributeLists(fieldDecl.AttributeLists, false);
                            WriteFieldType(fieldDecl, modifiers, typeSymbol);
                            writer.Write(" ");
                            atFirst = false;
                        }
                        else
                        {
                            writer.Write(", ");
                        }
                        WriteFieldIdentifier(fieldDecl, modifiers, typeSymbol, variableDecl);
                    }
                }
                if (!atFirst)
                {
                    writer.WriteLine(";");
                }
            }

            //
            // Print array variables
            //
            foreach (VariableDeclaratorSyntax variableDecl in fieldDecl.Declaration.Variables)
            {
                if (variableDecl.ArgumentList != null)
                {
                    WriteAttributeLists(fieldDecl.AttributeLists, false);
                    WriteFieldType(fieldDecl, modifiers, typeSymbol);
                    writer.Write("{0} ", variableDecl.ArgumentList.GetText());
                    WriteFieldIdentifier(fieldDecl, modifiers, typeSymbol, variableDecl);
                    writer.WriteLine(";");
                }
            }
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
        public void WriteDlangDefaultValue(SyntaxNode location, ITypeSymbol typeSymbol)
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
                    WriteDlangType(location, TypeContext.Default, typeSymbol);
                    writer.Write(")0)");
                    break;
                case TypeKind.Struct:
                    WriteDlangType(location, TypeContext.Default, typeSymbol);
                    writer.Write("()");
                    break;
                case TypeKind.TypeParameter:
                    throw new NotImplementedException();
                    //break;
                default:
                    throw new NotImplementedException(String.Format("WriteDlangDefaultValue (type kind '{0}')", typeSymbol.TypeKind));
            }
        }
        public void WriteDlangType(SyntaxNode location, TypeContext context, ITypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind == TypeKind.Array)
            {
                WriteDlangType(location, context, ((IArrayTypeSymbol)typeSymbol).ElementType);
                writer.Write("[]");
            }
            else if (typeSymbol.TypeKind == TypeKind.Pointer)
            {
                WriteDlangType(location, context, ((IPointerTypeSymbol)typeSymbol).PointedAtType);
                writer.Write("*");
            }
            else
            {
                INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
                int arity = (namedTypeSymbol == null) ? 0 : namedTypeSymbol.Arity;

                string moduleAndContainingType = generator.GetModuleAndContainingType(typeSymbol);
                DType dType = SemanticExtensions.DotNetToD(context,
                    moduleAndContainingType, typeSymbol.Name, (uint)arity);

                if (typeSymbol.Kind != SymbolKind.TypeParameter)
                {
                    if (typeSymbol.ContainingType != null)
                    {
                        if (!currentDeclContext.Inside(typeSymbol.ContainingType))
                        {
                            WriteDlangType(location, context, typeSymbol.ContainingType);
                            writer.Write(".");
                            context = TypeContext.AfterDot;
                        }
                    }
                    // Check if the type needs to be qualified
                    else if (context != TypeContext.AfterDot && !dType.isPrimitive)
                    {
                        // If there are multiple types with the same name
                        HashSet<string> sameNameModules;
                        if (!firstPass.modulesByTypeName.TryGetValue(
                            new TypeSymbolAndArity(dType.name, (uint)arity), out sameNameModules))
                        {
                            throw new SyntaxNodeException(location, String.Format(
                                "CodeBug: type '{0}' was not found in modulesByTypeName", typeSymbol));
                        }
                        if (sameNameModules.Count > 1)
                        {
                            writer.Write(moduleAndContainingType);
                            writer.Write(".");
                            context = TypeContext.AfterDot;
                        }
                    }
                }

                writer.Write(dType.name);
                if (arity > 0)
                {
                    writer.Write("{0}!(", arity);
                    bool atFirst = true;
                    foreach (ITypeSymbol genericTypeArg in namedTypeSymbol.TypeArguments)
                    {
                        if (atFirst) { atFirst = false; } else { writer.Write(","); }
                        WriteDlangType(location, TypeContext.Default, genericTypeArg);
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
            if (CSharpToD.generateDebug)
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
            WriteDlangType(cast.Type, TypeContext.Default, typeSymbol);
            writer.Write(")");
            Visit(cast.Expression);
        }


        // Returns: true if the expression is a namespace
        //          if it was a namespace, it will not have been printed
        public bool HandleMemberAccess(ExpressionSyntax memberExpression, SyntaxToken operatorToken)
        {
            SyntaxKind kind = memberExpression.Kind();

            if (kind == SyntaxKind.IdentifierName)
            {
                ISymbol symbol = currentFileModel.semanticModel.GetSymbolInfo(memberExpression).Symbol;
                if (symbol == null) throw new InvalidOperationException();
                if (symbol.Kind == SymbolKind.Namespace)
                {
                    if (CSharpToD.generateDebug)
                    {
                        writer.Write("/*RemovedNamespaceIdentifier '{0}'*/", memberExpression.GetText().ToString().Trim());
                    }
                    return true; // do not print the namespace
                }

                ITypeSymbol typeSymbol = symbol as ITypeSymbol;
                if (typeSymbol == null)
                {
                    if (CSharpToD.generateDebug)
                    {
                        writer.Write("/*MemberExpression:Identifier*/");
                    }
                    Visit(memberExpression);
                }
                else
                {
                    writer.Write("/*MemberExpression:Type*/");
                    WriteDlangType(memberExpression, TypeContext.Default, typeSymbol);
                }
                writer.Write(operatorToken.Text);
                return false;
            }

            if (kind == SyntaxKind.SimpleMemberAccessExpression)
            {
                var memberAccess = (MemberAccessExpressionSyntax)memberExpression;
                bool parentIsNamespace = HandleMemberAccess(memberAccess.Expression, memberAccess.OperatorToken);

                ISymbol symbol = currentFileModel.semanticModel.GetSymbolInfo(memberAccess.Name).Symbol;
                if (symbol == null) throw new InvalidOperationException();

                if (parentIsNamespace && symbol.Kind == SymbolKind.Namespace)
                {
                    return true; // do not print the namespace
                }

                ITypeSymbol typeSymbol = symbol as ITypeSymbol;
                if (typeSymbol == null)
                {
                    writer.Write(memberAccess.Name.Identifier.Text);
                }
                else
                {
                    writer.Write("/*MemberName:Type*/");
                    WriteDlangType(memberExpression, TypeContext.Default, typeSymbol);
                }

                writer.Write(operatorToken.Text);
                return false;
            }

            Visit(memberExpression);
            writer.Write(operatorToken.Text);
            return false;
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax memberAccess)
        {
            if (CSharpToD.generateDebug)
            {
                writer.Write("/*MemberAccessExpression(*/");
            }
            bool isNamespace = HandleMemberAccess(memberAccess.Expression, memberAccess.OperatorToken);
            if (CSharpToD.generateDebug)
            {
                SymbolInfo symbolInfo = currentFileModel.semanticModel.GetSymbolInfo(memberAccess.Expression);
                if (symbolInfo.Symbol == null) throw new InvalidOperationException();
                writer.Write("/*MemberAccessName:{0}*/", symbolInfo.Symbol.Kind);
            }
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
            { "Object", new DType(false, "__DotNet__Object") },
            {"Exception", new DType(false, "__DotNet__Exception") },
        };
        static readonly Dictionary<string, DType> PrimitiveSystemTypeMap = new Dictionary<string, DType>
        {
            {"byte", new DType(true, "ubyte") },
            {"sbyte", new DType(true, "byte") },
            {"char", new DType(true, "wchar") },

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

            { "Object", new DType(false, "__DotNet__Object") },
            {"Exception", new DType(false, "__DotNet__Exception") },
        };
        static readonly Dictionary<string, DType> PrimitiveSystemReflectionTypeMap = new Dictionary<string, DType>
        {
            {"TypeInfo", new DType(false, "__DotNet__TypeInfo") },
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
