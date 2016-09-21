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

    public struct KeyValues<T, K> : IEnumerable<KeyValuePair<T, List<K>>>
    {
        readonly Dictionary<T, List<K>> map;
        public KeyValues(Boolean igore)
        {
            this.map = new Dictionary<T, List<K>>();
        }
        public void Add(T key, K value)
        {
            List<K> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<K>();
                map.Add(key, list);
            }
            list.Add(value);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }
        public IEnumerator<KeyValuePair<T, List<K>>> GetEnumerator()
        {
            return map.GetEnumerator();

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
                writer.WriteLine(@"immutable string frameworkPath = `{0}`;",
                    Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    Path.Combine("..",
                    Path.Combine("..",
                    Path.Combine("mscorlib", "cs2d")))));


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
        compileCommand ~= "" -I"" ~ frameworkPath;
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
        compileCommand ~= "" "" ~ buildNormalizedPath(frameworkPath, ""mscorlib.lib"");
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

    public class CSharpFileModelNodes
    {
        public readonly CSharpFileModel fileModel;
        public readonly List<SyntaxList<AttributeListSyntax>> attributeLists = new List<SyntaxList<AttributeListSyntax>>();
        public readonly List<NamespaceDeclarationSyntax> namespaceDecls = new List<NamespaceDeclarationSyntax>();
        public CSharpFileModelNodes(CSharpFileModel fileModel)
        {
            this.fileModel = fileModel;
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
        public void Finish()
        {
            //
            // First Pass: find imports
            //
            FirstPassVisitor firstPass = new FirstPassVisitor();
            foreach (CSharpFileModelNodes fileModelNodes in fileModelNodeMap.Values)
            {
                foreach (NamespaceDeclarationSyntax namespaceDecl in fileModelNodes.namespaceDecls)
                {
                    firstPass.currentFileModel = fileModelNodes.fileModel;
                    firstPass.Visit(namespaceDecl);
                }
            }

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
                // Add Include Files
                //
                if(includeSourceFiles != null)
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

                foreach (KeyValuePair<INamespaceSymbol,List<ITypeSymbol>> namespaceAndType in firstPass.typesByNamespace)
                {
                    if (namespaceAndType.Key.Name != this.@namespace)
                    {
                        writer.WriteLine("import {0};", namespaceAndType.Key);
                        foreach(ITypeSymbol typeSymbol in namespaceAndType.Value)
                        {
                            writer.WriteCommentedLine(typeSymbol.Name);
                        }
                    }
                }
                DlangVisitorGenerator visitor = new DlangVisitorGenerator(this, writer);
                foreach (CSharpFileModelNodes fileModelNodes in fileModelNodeMap.Values)
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
                    foreach (NamespaceDeclarationSyntax namespaceDecl in fileModelNodes.namespaceDecls)
                    {
                        visitor.currentFileModel = fileModelNodes.fileModel;
                        visitor.Visit(namespaceDecl);
                    }
                }
            }
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
        }        public void AddNamespace(CSharpFileModel fileModel, NamespaceDeclarationSyntax node)
        {
            GetFileModelNodes(fileModel).namespaceDecls.Add(node);
        }
        public void AddIncludeFile(String includeFile)
        {
            if(includeSourceFiles == null)
            {
                includeSourceFiles = new List<string>();
            }
            includeSourceFiles.Add(includeFile);
        }
    }
    class FirstPassVisitor : CSharpSyntaxVisitor
    {
        public CSharpFileModel currentFileModel;


        readonly HashSet<ITypeSymbol> typesAlreadyAdded = new HashSet<ITypeSymbol>();
        public readonly KeyValues<INamespaceSymbol, ITypeSymbol> typesByNamespace =
            new KeyValues<INamespaceSymbol, ITypeSymbol>(true);

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
                typesByNamespace.Add(typeSymbol.ContainingNamespace, typeSymbol);

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

        void VisitTypeDeclaration(TypeDeclarationSyntax node)
        {
            if (node.BaseList != null)
            {
                foreach (BaseTypeSyntax type in node.BaseList.Types)
                {
                    TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(type.Type);
                    if (typeInfo.Type == null)
                    {
                        throw new InvalidOperationException();
                    }
                    AddNamespaceFrom(typeInfo.Type);
                }
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
    }
    class DlangVisitorGenerator : CSharpSyntaxVisitor
    {
        readonly DlangGenerator generator;
        public readonly DlangWriter writer;
        public CSharpFileModel currentFileModel;
        
        public DlangVisitorGenerator(DlangGenerator generator, DlangWriter writer)
        {
            this.generator = generator;
            this.writer = writer;
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


        static readonly Dictionary<string, string> PrimitiveTypeMap = new Dictionary<string, string>
        {
            {"Exception", "DotNetException" },
            {"Byte"  , "ubyte" },
            {"SByte" , "byte" },
            {"Char"  , "wchar" },
            {"UInt16", "ushort" },
            {"Int16" , "short" },
            {"UInt32", "uint" },
            {"Int32" , "int" },
            {"UInt64", "ulong" },
            {"Int64" , "long" },
        };
        static void WriteDlangTypeName(DlangWriter writer, String @namespace, TypeDeclarationSyntax typeDecl)
        {
            UInt32 genericTypeCount = 0;
            if(typeDecl.TypeParameterList != null)
            {
                genericTypeCount = (uint)typeDecl.TypeParameterList.Parameters.Count;
            }
            WriteDlangTypeName(writer, @namespace, typeDecl.Identifier.ToString(), genericTypeCount);
        }
        static void WriteDlangTypeName(DlangWriter writer, String @namespace, String identifier, UInt32 genericTypeCount)
        {
            // Special case for System.Exception
            if (@namespace == "System")
            {
                if(genericTypeCount == 0)
                {
                    String dlangTypeName;
                    if(PrimitiveTypeMap.TryGetValue(identifier, out dlangTypeName))
                    {
                        writer.Write(dlangTypeName);
                        return;
                    }
                }
            }

            writer.Write(identifier);
            if (genericTypeCount > 0)
            {
                writer.Write("{0}", genericTypeCount);
            }
        }
        static void WriteDlangTypeName(DlangWriter writer, ITypeSymbol typeSymbol)
        {
            // Special case for System.Exception
            if (typeSymbol.ContainingNamespace.Name == "System")
            {
                // TODO: BUG: This will match generic types
                String dlangTypeName;
                if (PrimitiveTypeMap.TryGetValue(typeSymbol.Name, out dlangTypeName))
                {
                    writer.Write(dlangTypeName);
                    return;
                }
            }

            writer.Write(typeSymbol.Name);
            INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
            if (namedTypeSymbol != null)
            {
                if (namedTypeSymbol.Arity > 0)
                {
                    writer.Write("{0}!(", namedTypeSymbol.Arity);
                    bool atFirst = true;
                    foreach (ITypeSymbol genericTypeArg in namedTypeSymbol.TypeArguments)
                    {
                        if (atFirst) { atFirst = false; } else { writer.Write(","); }
                        WriteDlangTypeName(writer, genericTypeArg);
                    }
                    writer.Write(")");
                }
            }
        }


        void VisitTypeDeclarationSyntax(bool isClass, TypeDeclarationSyntax node)
        {
            if (node.AttributeLists.HasItems())
            {
                foreach(var attr in node.AttributeLists)
                {
                    writer.WriteCommentedLine(attr.GetText().ToString().Trim());
                }
            }

            ModifierCategories modifiers = new ModifierCategories(node.Modifiers);
            if (modifiers.visibility == null)
            {
                // default visibility in C# is private, default in D is public
                writer.Write("private ");
            }
            else
            {
                writer.Write(modifiers.visibility);
                writer.Write(" ");
            }
            if (modifiers.@static)
            {
                writer.Write("static ");
            }
            if (modifiers.@abstract)
            {
                writer.Write("abstract ");
            }

            if(isClass)
            {
                writer.Write("class ");
            }
            else
            {
                writer.Write("interface ");
            }
            WriteDlangTypeName(writer, generator.@namespace, node);
            if (node.TypeParameterList != null)
            {
                writer.Write("(");
                bool atFirst = true;
                foreach (TypeParameterSyntax typeParam in node.TypeParameterList.Parameters)
                {
                    if (atFirst) { atFirst = false; } else { writer.Write(","); }
                    writer.Write(typeParam.Identifier.Text);
                }
                writer.Write(")");
            }
            if (node.BaseList != null)
            {
                writer.Write(" : ");
                bool atFirst = true;
                foreach (BaseTypeSyntax type in node.BaseList.Types)
                {
                    if (atFirst) { atFirst = false; } else { writer.Write(", "); }

                    TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(type.Type);
                    if(typeInfo.Type == null)
                    {
                        throw new InvalidOperationException();
                    }

                    INamedTypeSymbol namedType = (INamedTypeSymbol)typeInfo.Type;
                    WriteDlangTypeName(writer, namedType);
                }
            }
            if (node.ConstraintClauses.HasItems())
            {
                foreach (TypeParameterConstraintClauseSyntax constraint in node.ConstraintClauses)
                {
                    writer.WriteCommentedInline(constraint.GetText().ToString().Trim());
                }
            }
            writer.WriteLine();
            writer.WriteLine("{");
            writer.Tab();


            //foreach (var member in node.Members)
            //{
            //    Visit(member);
            //}
            writer.WriteLine("// TODO: generate class contents");

            writer.Untab();
            writer.WriteLine("}");

        }
        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            VisitTypeDeclarationSyntax(true, node);
        }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            VisitTypeDeclarationSyntax(false, node);
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            writer.WriteLine("// TODO: generate struct {0}", node.Identifier.Text);
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
    }


    struct ModifierCategories
    {
        public string visibility;
        public bool @static;
        public bool @abstract;

        public ModifierCategories(SyntaxTokenList modifiers)
        {
            this.visibility = null;
            this.@static = false;
            this.@abstract = false;

            foreach (SyntaxToken modifier in modifiers)
            {
                String text = modifier.Text;
                if (text == "public" || text == "private")
                {
                    if (this.visibility != null)
                    {
                        throw new InvalidOperationException(String.Format("visibility set twice '{0}' and '{1}'",
                            this.visibility, visibility));
                    }
                    this.visibility = text;
                }
                else if (text == "internal")
                {
                    if (this.visibility != null)
                    {
                        throw new InvalidOperationException(String.Format("visibility set twice '{0}' and '{1}'",
                            this.visibility, visibility));
                    }
                    this.visibility = "public"; // treat 'internal' as public
                }
                else if (text == "static")
                {
                    if (this.@static)
                    {
                        throw new InvalidOperationException("static set twice");
                    }
                    this.@static = true;
                }
                else if (text == "abstract")
                {
                    if (this.@abstract)
                    {
                        throw new InvalidOperationException("abstract set twice");
                    }
                    this.@abstract = true;
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
