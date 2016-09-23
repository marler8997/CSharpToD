using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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

    class DBuildGenerator
    {
        public static void MakeDProjectFile(Config config, ProjectModels[] projectModelsArray)
        {
            String projectFile = Path.Combine(CSharpToD.generatedCodePath, "build.d");
            using (DlangWriter writer = new DlangWriter(new BufferedNativeFileSink(
                NativeFile.Open(projectFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[512])))
            {
                writer.WriteLine(@"import std.stdio;
import std.getopt  : getopt;
import std.format  : format;
import std.process : spawnShell, wait;
import std.path    : setExtension, buildNormalizedPath;

enum OutputType {Library,Exe}
");

                writer.WriteLine(@"immutable string rootPath = `{0}`;", CSharpToD.generatedCodePath);
                writer.WriteLine(@"immutable string mscorlibPath = `{0}`;", CSharpToD.mscorlibPath);


                writer.WriteLine(@"immutable string[] sourceFiles = [");
                writer.Tab();
                foreach (DlangGenerator generator in WorkspaceModels.Generators)
                {
                    writer.WriteLine("`{0}`,", generator.filenameFullPath);
                }
                writer.Untab();
                writer.WriteLine("];");
                writer.WriteLine(@"immutable outputName = ""{0}"";", config.outputName);
                writer.WriteLine(@"immutable outputType = OutputType.{0};", config.outputType);
                writer.WriteLine(@"enum DEFAULT_NO_MSCORLIB = {0};", config.noMscorlib ? "true" : "false");
                writer.WriteLine(@"immutable string[] includePaths = [");
                writer.Tab();
                foreach(String includePath in config.includePaths)
                {
                    writer.WriteLine("`{0}`,", Path.Combine(CSharpToD.conversionRoot, includePath));
                }
                writer.Untab();
                writer.WriteLine("];");
                writer.WriteLine(@"immutable string[] libraries = [");
                writer.Tab();
                foreach (String library in config.libraries)
                {
                    writer.WriteLine("`{0}`,", Path.Combine(CSharpToD.conversionRoot, library));
                }
                writer.Untab();
                writer.WriteLine("];");

                writer.WriteLine(@"
int tryRun(string command)
{
    writefln(""[RUN] '%s'"", command);
    auto pid = spawnShell(command);
    return wait(pid);
}
void run(string command)
{
    auto exitCode = tryRun(command);
    if(exitCode) {
        writefln(""Error: last [RUN] command failed (exit code %s)"", exitCode);
    }
}
int main(string[] args)
{
    bool noMscorlib = DEFAULT_NO_MSCORLIB;
    getopt(args,
        ""no-mscorlib"", &noMscorlib);

    string[] objectFiles = new string[sourceFiles.length];
    foreach(i, sourceFile; sourceFiles) {
        objectFiles[i] = sourceFile.setExtension(""obj"");
    }

    //
    // Compile
    //
    string compileCommand = format(""dmd -c -I%s"", rootPath);
    if(!noMscorlib) {
        compileCommand ~= "" -I"" ~ mscorlibPath;
    }
    foreach(includePath; includePaths) {
        compileCommand ~= "" -I"" ~ buildNormalizedPath(includePath);
    }

    foreach(i, sourceFile; sourceFiles) {
        if(tryRun(format(""%s -of%s %s"", compileCommand, objectFiles[i], sourceFile))) {
            return 1;
        }
    }

    //
    // Link
    //
    string linkCommand = ""dmd"";
    if(outputType == OutputType.Library) {
        linkCommand ~= "" -lib"";
    }
    if(outputName.length > 0) {
        linkCommand ~= format("" -of%s"", buildNormalizedPath(rootPath, outputName));
    } else {
        linkCommand ~= format("" -od%s"", rootPath);
    }
    if(!noMscorlib) {
        compileCommand ~= "" "" ~ buildNormalizedPath(mscorlibPath, ""mscorlib.lib"");
    }
    foreach(library; libraries) {
        linkCommand ~= format("" %s"", library);
    }
    foreach(objectFile; objectFiles) {
        linkCommand ~= format("" %s"", objectFile);
    }
    if(tryRun(linkCommand)) {
        return 1;
    }

    writeln(""Success"");
    return 0;
}
");
            }
        }
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
                else if(putInPackage)
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
                FirstPassVisitor firstPass = new FirstPassVisitor(writer);
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
                    foreach(String includeSourceFile in includeSourceFiles)
                    {
                        String includeSourceFullPath = Path.Combine(CSharpToD.conversionRoot, includeSourceFile);
                        if(!File.Exists(includeSourceFullPath))
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

                foreach (KeyValuePair<String,HashSet<ITypeSymbol>> namespaceAndType in firstPass.typesByNamespace)
                {
                    if (namespaceAndType.Key != this.@namespace)
                    {
                        if (namespaceAndType.Key.Length == 0)
                        {
                            writer.WriteLine("import __NoNamespace__ /*:");
                        }
                        else
                        {
                            writer.WriteLine("import {0} /*:", namespaceAndType.Key);
                        }
                        writer.Tab();
                        uint typeIndex = 0;
                        uint lastIndex = (uint)namespaceAndType.Value.Count - 1;
                        foreach(ITypeSymbol typeSymbol in namespaceAndType.Value)
                        {
                            writer.WriteDlangTypeName(typeSymbol);
                            if(typeIndex < lastIndex)
                            {
                                writer.WriteLine(",");
                            }
                            else
                            {
                                writer.WriteLine("*/;");
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
                        foreach(AttributeListSyntax attrList in attrListList)
                        {
                            writer.WriteIgnored(attrList.GetText());
                        }
                    }
                    foreach (MemberDeclarationSyntax decl in fileModelNodes.decls)
                    {
                        visitor.currentFileModel = fileModelNodes.fileModel;
                        visitor.Visit(decl);
                    }
                }
            }
        }
    }
    class FirstPassVisitor : CSharpSyntaxVisitor
    {
        readonly DlangWriter writer;
        public CSharpFileModel currentFileModel;
        
        readonly HashSet<ITypeSymbol> typesAlreadyAdded = new HashSet<ITypeSymbol>();
        public readonly KeyUniqueValues<string, ITypeSymbol> typesByNamespace =
            new KeyUniqueValues<string, ITypeSymbol>(true);

        public readonly KeyValues<string, TypeDeclarationSyntax> partialTypes =
            new KeyValues<string, TypeDeclarationSyntax>(true);

        public FirstPassVisitor(DlangWriter writer)
        {
            this.writer = writer;
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
        
        void AddNamespaceFrom(ITypeSymbol typeSymbol)
        {
            if(!typesAlreadyAdded.Contains(typeSymbol))
            {
                typesAlreadyAdded.Add(typeSymbol);
                typesByNamespace.Add(typeSymbol.ContainingModule(), typeSymbol);
                //Console.WriteLine("Namespace '{0}', from symbol '{1}'", typeSymbol.ContainingModule(), typeSymbol.Name);
                //writer.WriteCommentedLine(String.Format("Namespace '{0}', from symbol '{1}'", typeSymbol.ContainingModule(), typeSymbol.Name));


                INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
                if (namedTypeSymbol != null && namedTypeSymbol.Arity > 0)
                {
                    foreach (ITypeSymbol genericTypeArg in namedTypeSymbol.TypeArguments)
                    {
                        AddNamespaceFrom(genericTypeArg);
                    }
                }
            }
        }

        void VisitTypeDeclaration(TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl.Modifiers.ContainsPartial())
            {
                partialTypes.Add(typeDecl.Identifier.Text, typeDecl);
            }
            if (typeDecl.BaseList != null)
            {
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
        }
        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            VisitTypeDeclaration(node);
        }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            VisitTypeDeclaration(node);
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            VisitTypeDeclaration(node);
        }
    }

    struct TypeContext
    {
        public readonly TypeDeclarationSyntax typeDecl;
        public readonly bool isStatic;
        public TypeContext(TypeDeclarationSyntax typeDecl, bool isStatic)
        {
            this.typeDecl = typeDecl;
            this.isStatic = isStatic;
        }
    }

    class DlangVisitorGenerator : CSharpSyntaxVisitor
    {
        readonly DlangGenerator generator;
        public readonly DlangWriter writer;
        public CSharpFileModel currentFileModel;
        readonly FirstPassVisitor firstPass;

        readonly Stack<TypeContext> currentTypeContext =
            new Stack<TypeContext>();

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


        static void WriteAttributes(DlangWriter writer, SyntaxList<AttributeListSyntax> attributeLists)
        {
            foreach (var attributeList in attributeLists)
            {
                writer.WriteCommentedLine(attributeList.GetText().ToString().Trim());
            }
        }
        bool InStaticContext()
        {
            return currentTypeContext.Count > 0 && currentTypeContext.Peek().isStatic;
        }

        enum TypeDeclType {
            Class = 0,
            Interface = 1,
            Struct = 2,
        }

        void VisitTypeDeclarationSyntax(TypeDeclType typeDeclType, TypeDeclarationSyntax typeDecl)
        {
            ModifierCategories modifiers = new ModifierCategories(typeDecl.Modifiers);
            List<TypeDeclarationSyntax> partialTypeDecls;

            if (modifiers.partial)
            {
                partialTypeDecls = firstPass.partialTypes[typeDecl.Identifier.Text];
                if(partialTypeDecls.IndexOf(typeDecl) != 0)
                {
                    // Only generate the contents for the first type
                    return;
                }
                foreach (TypeDeclarationSyntax partialTypeDecl in partialTypeDecls)
                {
                    WriteAttributes(writer, partialTypeDecl.AttributeLists);
                }
            }
            else
            {
                partialTypeDecls = null;
                WriteAttributes(writer, typeDecl.AttributeLists);
            }

            if (modifiers.dlangVisibility == null)
            {
                // default visibility for classes in in C# is internal, default in D is public
                // since internal has no meaning after conversion to D, it must be public.
                // Since default in D is public, there is no need for a modifier.
                //writer.Write("private ");
            }
            else
            {
                writer.Write(modifiers.dlangVisibility);
                writer.Write(" ");
            }
            if(currentTypeContext.Count > 0)
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
            writer.WriteDlangTypeName(generator.@namespace, typeDecl);
            if (typeDecl.TypeParameterList != null)
            {
                writer.Write("(");
                bool atFirst = true;
                foreach (TypeParameterSyntax typeParam in typeDecl.TypeParameterList.Parameters)
                {
                    if (atFirst) { atFirst = false; } else { writer.Write(","); }
                    writer.Write(typeParam.Identifier.Text);
                }
                writer.Write(")");
            }
            // TODO: loop through base list of all partial classes
            if (typeDecl.BaseList != null)
            {
                writer.Write(" : ");
                bool atFirst = true;
                foreach (BaseTypeSyntax type in typeDecl.BaseList.Types)
                {
                    if (atFirst) { atFirst = false; } else { writer.Write(", "); }

                    TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(type.Type);
                    if(typeInfo.Type == null)
                    {
                        throw new InvalidOperationException();
                    }

                    INamedTypeSymbol namedType = (INamedTypeSymbol)typeInfo.Type;
                    writer.WriteDlangTypeName(namedType);
                }
            }
            if (typeDecl.ConstraintClauses.HasItems())
            {
                foreach (TypeParameterConstraintClauseSyntax constraint in typeDecl.ConstraintClauses)
                {
                    writer.WriteCommentedInline(constraint.GetText().ToString().Trim());
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
                currentTypeContext.Push(new TypeContext(typeDecl, modifiers.@static));
                if (partialTypeDecls == null)
                {
                    foreach (var member in typeDecl.Members)
                    {
                        Visit(member);
                    }
                }
                else
                {
                    foreach (TypeDeclarationSyntax partialTypeDecl in partialTypeDecls)
                    {
                        foreach (var member in partialTypeDecl.Members)
                        {
                            Visit(member);
                        }
                    }
                }
            }
            finally
            {
                currentTypeContext.Pop();
            }

            writer.Untab();
            writer.WriteLine("}");
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            VisitTypeDeclarationSyntax(TypeDeclType.Class, node);
        }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            //Console.WriteLine("Generate Interface: {0}", node.Identifier);
            VisitTypeDeclarationSyntax(TypeDeclType.Interface, node);
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            //writer.WriteLine("// TODO: generate struct {0}", node.Identifier.Text);
            VisitTypeDeclarationSyntax(TypeDeclType.Struct, node);
        }
        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            foreach(AttributeListSyntax attr in node.AttributeLists)
            {
                writer.WriteCommentedLine(String.Format("TODO: {0}", attr.GetText().ToString().Trim()));
            }

            writer.WriteLine("// TODO: generate delegate");
        }
        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            writer.WriteLine("// TODO: generate enum '{0}'", node.Identifier.Text);
        }


        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            writer.WriteCommentedLine(String.Format("TODO: generate fields"));
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
            if(namespaceSymbol == null || namespaceSymbol.Name.Length == 0)
            {
                return "__NoNamespace__";
            }
            return namespaceSymbol.ToString();
        }
    }

    class SyntaxNodeException : Exception
    {
        public SyntaxNodeException(SyntaxNode node, String message)
            : base(String.Format("{0}({1}): {2}", node.SyntaxTree.FilePath,
                node.GetLocation().GetLineSpan(), message))
        {
        }
        public SyntaxNodeException(SyntaxToken token, String message)
            : base(String.Format("{0}({1}): {2}", token.SyntaxTree.FilePath,
                token.GetLocation().GetLineSpan(), message))
        {
        }
    }


    struct ModifierCategories
    {
        public string dlangVisibility;
        public bool partial;
        public bool @static;
        public bool @abstract;
        public bool @sealed;
        public bool @unsafe;

        public ModifierCategories(SyntaxTokenList modifiers)
        {
            this.dlangVisibility = null;
            this.partial = false;
            this.@static = false;
            this.@abstract = false;
            this.@sealed = false;
            this.@unsafe = false;

            foreach (SyntaxToken modifier in modifiers)
            {
                String text = modifier.Text;
                if (text == "public" || text == "private" || text == "protected")
                {
                    if (this.dlangVisibility != null)
                    {
                        throw new SyntaxNodeException(modifier, String.Format("visibility set twice '{0}' and '{1}'",
                            this.dlangVisibility, text));
                    }
                    this.dlangVisibility = text;
                }
                else if(text == "protected")
                {
                    if (this.dlangVisibility != null)
                    {
                        throw new SyntaxNodeException(modifier, String.Format("visibility set twice '{0}' and '{1}'",
                            this.dlangVisibility, text));
                    }
                    this.dlangVisibility = text;
                }
                else if (text == "internal")
                {
                    if (this.dlangVisibility != null)
                    {
                        if (this.dlangVisibility == "protected")
                        {
                            this.dlangVisibility = "public "; // Not sure what "protected internal" maps to, just use "public" for now
                        }
                        else
                        {
                            throw new SyntaxNodeException(modifier, String.Format("visibility set twice '{0}' and '{1}'",
                                this.dlangVisibility, text));
                        }
                    }
                    else
                    {
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
                else
                {
                    throw new NotImplementedException(String.Format(
                        "Modifier '{0}' not implemented", text));
                }
            }
        }
    }
}
