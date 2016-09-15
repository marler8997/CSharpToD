using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CS2D
{
    static class DlangGenerators
    {
        static readonly Dictionary<string, DlangGenerator> Generators = new Dictionary<string, DlangGenerator>();
        public static DlangGenerator GetOrCreatGenerator()
        {
            return GetOrCreatGenerator("");
        }
        public static DlangGenerator GetOrCreatGenerator(String @namespace)
        {
            DlangGenerator generator;
            lock (Generators)
            {
                if (!Generators.TryGetValue(@namespace, out generator))
                {
                    generator = new DlangGenerator(@namespace);
                    Generators.Add(@namespace, generator);
                }
            }
            return generator;

        }
        public static void FinishGenerators()
        {
            lock (Generators)
            {
                foreach (DlangGenerator generator in Generators.Values)
                {
                    generator.Finish();
                }
            }
            MakeDProjectFile();
        }

        static readonly Object directoryCreatorLock = new Object();
        public static void SynchronizedCreateDirectoryFor(String filenameFullPath)
        {
            SynchronizedCreateDirectory(Path.GetDirectoryName(filenameFullPath));
        }
        public static void SynchronizedCreateDirectory(String directoryName)
        {
            lock (directoryCreatorLock)
            {
                if (!Directory.Exists(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
            }
        }

        static void MakeDProjectFile()
        {
            String projectFile = Path.Combine(CS2DMain.dlangDirectory, "build.d");
            using (DlangWriter writer = new DlangWriter(new BufferedNativeFileSink(
                NativeFile.Open(projectFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[512])))
            {
                writer.WriteLine(@"import std.stdio;
import std.format  : format;
import std.process : spawnShell, wait;
import std.path    : setExtension;
");

                writer.WriteLine(@"immutable sourceFiles = [");
                writer.Tab();
                // Don't need to lock, all Tasks/Threads should be done by this point
                foreach (DlangGenerator generator in Generators.Values)
                {
                    writer.WriteLine("`{0}`,", generator.fullPathFilename);
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
    foreach(sourceFile; sourceFiles) {
        run(format(""dmd -c -of%s %s"", sourceFile.setExtension(""obj""), sourceFile));
    }
    return 0;
}
");
            }
        }
    }

    class DlangGenerator
    {
        public readonly String @namespace;
        public readonly String fullPathFilename;
        readonly DlangWriter writer;

        public DlangGenerator(String @namespace)
        {
            this.@namespace = @namespace;
            int lastDotIndex = @namespace.LastIndexOf('.');

            String relativeFilename;

            if (lastDotIndex >= 0)
            {
                String relativePath = @namespace.Remove(lastDotIndex).Replace('.', Path.DirectorySeparatorChar);
                String filename = @namespace.Substring(lastDotIndex + 1) + ".d";
                relativeFilename = Path.Combine(relativePath, filename);
            }
            else
            {
                if (@namespace.Length == 0)
                {
                    // TODO: come up with a filename for this
                    relativeFilename = "__NoNamespace__.d";
                }
                else
                {
                    relativeFilename = @namespace + ".d";
                }
            }
            fullPathFilename = Path.Combine(CS2DMain.dlangDirectory, relativeFilename);
            DlangGenerators.SynchronizedCreateDirectoryFor(fullPathFilename);
            Console.WriteLine("Creating D Source File '{0}'...", fullPathFilename);
            writer = new DlangWriter(new BufferedNativeFileSink(
                NativeFile.Open(fullPathFilename, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new byte[512]));

            if (@namespace.Length == 0)
            {
                writer.WriteLine("// module __NoNamespace__;");
            }
            else
            {
                writer.WriteLine("module {0};", @namespace);
            }
        }
        
        public void Finish()
        {
            {
                DlangVisitorGenerator visitor = new DlangVisitorGenerator(writer);
                foreach (NamespaceDecl namespaceDecl in namespaceDecls)
                {
                    visitor.currentFileModel = namespaceDecl.fileModel;
                    visitor.Visit(namespaceDecl.node);
                }
                namespaceDecls.Clear();
            }

            // TODO: write any close braces and what not
            writer.Dispose();
        }
        public void ProcessAttributeLists(CSharpFileModel model, SyntaxList<AttributeListSyntax> attributeLists)
        {
            lock(@namespace)
            {
                foreach(AttributeListSyntax attributeList in attributeLists)
                {
                    writer.WriteIgnored(attributeList.GetText());
                }
            }
        }

        struct NamespaceDecl
        {
            public readonly CSharpFileModel fileModel;
            public readonly NamespaceDeclarationSyntax node;
            public NamespaceDecl(CSharpFileModel fileModel, NamespaceDeclarationSyntax node)
            {
                this.fileModel = fileModel;
                this.node = node;
            }
        }
        readonly List<NamespaceDecl> namespaceDecls = new List<NamespaceDecl>();
        public void AddNamespace(CSharpFileModel model, NamespaceDeclarationSyntax node)
        {
            namespaceDecls.Add(new NamespaceDecl(model, node));
        }
    }
    
    class DlangVisitorGenerator : CSharpSyntaxVisitor
    {
        public readonly DlangWriter writer;
        public CSharpFileModel currentFileModel;
        public DlangVisitorGenerator(DlangWriter writer)
        {
            this.writer = writer;
        }
        public override void DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException(String.Format("DlangVisitorGenerator for {0}", node.GetType().Name));
        }
        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            if (node.Usings.HasItems())
            {
                throw new NotImplementedException();
            }
            if (node.Externs.HasItems())
            {
                throw new NotImplementedException();
            }
            foreach (var member in node.Members)
            {
                Visit(member);
            }
        }
        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            writer.WriteLine("// TODO: generate class {0}", node.Identifier.Text);
            /*
            foreach (var modifier in node.Modifiers)
            {
                writer.Write(modifier.Text);
                writer.Write(" ");
            }

            writer.WriteLine("class {0}", node.Identifier.Text);
            writer.WriteLine("{");
            writer.Tab();

            if (node.AttributeLists.HasItems())
            {
                throw new NotImplementedException();
            }
            if (node.BaseList != null)
            {
                throw new NotImplementedException();
            }
            if (node.ConstraintClauses.HasItems())
            {
                throw new NotImplementedException();
            }
            if (node.TypeParameterList != null)
            {
                throw new NotImplementedException();
            }
            
            //foreach (var member in node.Members)
            //{
            //    Visit(member);
            //}
            writer.WriteLine("// TODO: generate class contents");

            writer.Untab();
            writer.WriteLine("}");
            */
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            writer.WriteLine("// TODO: generate struct {0}", node.Identifier.Text);
        }
        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            writer.WriteLine("// TODO: generate delegate");
        }
    }

    public class DlangWriter : IDisposable
    {
        readonly BufferedNativeFileSink sink;

        uint prefixSpaceCount;
        bool lineStarted;

        public DlangWriter(BufferedNativeFileSink sink)
        {
            this.sink = sink;
        }
        public void Dispose()
        {
            sink.Dispose();
        }


        public void WriteIgnored(SourceText text)
        {
            foreach (TextLine line in text.Lines)
            {
                var trimmed = line.ToString().Trim();
                if (!String.IsNullOrEmpty(trimmed))
                {
                    Write("// Ignored: ");
                    WriteLine(trimmed);
                }
            }
        }

        unsafe void WriteLinePrefix()
        {
            byte* spaces = stackalloc byte[(int)prefixSpaceCount];
            StandardC.memset(spaces, ' ', (int)prefixSpaceCount);
            sink.Put(spaces, prefixSpaceCount);
            lineStarted = true;
        }

        public void Tab()
        {
            prefixSpaceCount += 4;
        }
        public void Untab()
        {
            if (prefixSpaceCount == 0)
            {
                throw new InvalidOperationException("CodeBug: Untab called more than Tab");
            }
            prefixSpaceCount -= 4;
        }


        public void WriteCommented(String stuff)
        {
            if (stuff.Contains("\n"))
            {
                throw new NotImplementedException();
            }
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            sink.Put("/*");
            sink.Put(stuff);
            sink.Put("*/");
        }

        public void Write(String message)
        {
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            sink.Put(message);
        }
        public void Write(String fmt, params Object[] obj)
        {
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            // TODO: created a formattedWrite instead of calling String.Format
            sink.Put(String.Format(fmt, obj));
        }


        public void WriteLine(String message)
        {
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            sink.PutLine(message);
            lineStarted = false;
        }
        public void WriteLine(String fmt, params Object[] obj)
        {
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            // TODO: created a formattedWrite instead of calling String.Format
            sink.PutLine(String.Format(fmt, obj));
            lineStarted = false;
        }
    }
}
