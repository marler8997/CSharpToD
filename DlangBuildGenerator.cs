using System;
using System.IO;
using Microsoft.CodeAnalysis;

namespace CSharpToD
{
    class DlangBuildGenerator
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
struct Project
{
    string outputName;
    OutputType outputType;
    string[] sourceFiles;
}
");

                writer.WriteLine(@"immutable string rootPath = `{0}`;", CSharpToD.generatedCodePath);

                writer.WriteLine(@"immutable string mscorlibPath = `{0}`;", CSharpToD.mscorlibPath);
                writer.WriteLine(@"enum DEFAULT_NO_MSCORLIB = {0};", config.noMscorlib ? "true" : "false");

                writer.WriteLine(@"immutable string[] includePaths = [");
                writer.Tab();
                foreach (String includePath in config.includePaths)
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

                {
                    writer.WriteLine(@"immutable Project[] projects = [");
                    writer.Tab();
                    foreach (ProjectModels project in projectModelsArray)
                    {
                        writer.WriteLine("immutable Project(\"{0}\", OutputType.{1}, [",
                            project.assemblyPackageName, project.outputType);
                        writer.Tab();

                        DlangGenerator[] generators = System.Linq.Enumerable.ToArray(project.namespaceGeneratorMap.Values);
                        // Sort so that the source files are always in the same order
                        // no matter the timing
                        Array.Sort(generators);
                        foreach (DlangGenerator generator in generators)
                        {
                            // TODO: make these files relative
                            writer.WriteLine("`{0}`,", generator.filenameFullPath);
                        }
                        writer.Untab();
                        writer.WriteLine("]),");
                    }
                    writer.Untab();
                    writer.WriteLine("];");
                }

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
    bool compileSingleFiles = false;
    getopt(args,
        ""no-mscorlib"", &noMscorlib,
        ""compile-single-files"", &compileSingleFiles);

    foreach(project; projects)
    {
        writefln(""Building project '%s'..."", project.outputName);

        string[] objectFiles = new string[project.sourceFiles.length];
        foreach(i, sourceFile; project.sourceFiles) {
            objectFiles[i] = sourceFile.setExtension(""obj"");
        }

        string compileCommand = format(""dmd -I%s"", rootPath);
        if(!noMscorlib) {
            compileCommand ~= "" -I"" ~ mscorlibPath;
        }
        foreach(includePath; includePaths) {
            compileCommand ~= "" -I"" ~ buildNormalizedPath(includePath);
        }

        string linkArguments = """";
        if(project.outputType == OutputType.Library) {
            linkArguments ~= "" -lib"";
        }
        if(!noMscorlib) {
            linkArguments ~= "" "" ~ buildNormalizedPath(mscorlibPath, ""mscorlib.lib"");
        }
        foreach(library; libraries) {
            linkArguments ~= format("" %s"", library);
        }
        linkArguments ~= format("" -of%s"", buildNormalizedPath(rootPath, project.outputName));

        // The compileSingleFiles option is MUCH slower, but I'm keeping it for now
        // because compiling all source files together could cause a machine to run out of memory
        if(compileSingleFiles)
        {
            // Compile
            foreach(i, sourceFile; project.sourceFiles) {
                if(tryRun(format(""%s -c -of%s %s"", compileCommand, objectFiles[i], sourceFile))) {
                    return 1;
                }
            }

            // Link
            string linkCommand = ""dmd""~linkArguments;
            foreach(objectFile; objectFiles) {
                linkCommand ~= format("" %s"", objectFile);
            }
            if(tryRun(linkCommand)) {
                return 1;
            }
        } else {
            string compileAndLinkCommand = compileCommand~linkArguments;
            foreach(i, sourceFile; project.sourceFiles) {
                compileAndLinkCommand ~= "" ""~sourceFile;
            }
            if(tryRun(compileAndLinkCommand)) {
                return 1;
            }
        }
    }
    writeln(""Success"");
    return 0;
}
");
            }
        }
    }
}
