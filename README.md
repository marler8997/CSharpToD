
CSharpToD
================================================================================
A tool that converts C# source code to D source code.  It uses the Roslyn
libraries to parse and analyze the C# code.

How to Use
--------------------------------------------------------------------------------
Pick a parent directory for a set of C# projects that you want to convert.
```
/SomePath/MyCSharpProjects
/SomePath/MyCSharpProjects/ProjectA
/SomePath/MyCSharpProjects/ProjectB
```

Let's pick "/SomePath".  Create the file "cs2d.config" in this directory:
```
/SomePath/cs2d.config
```

This file contains various settings, but the most important peices are the
projects and solutions, i.e.
```
Project MyCSharpProjects/ProjectA/ProjectA.csproj
Project MyCSharpProjects/ProjectB/ProjectB.csproj
Solution MyCSharpProjects/Solution1/Solution1.csproj
```

By default, when the projects are converted to D, they will get generated in
a directory named "cs2d" in the same path as the config file ("/SomePath/cs2d"
in this case).  Now we're ready to run:
```
> csharptod.exe /SomePath/cs2d.config
```

This command will convert the code to D, and then build the generated D code
into libraries and/or executables.  To build the D code, csharptod.exe
generates a D script that will be in "cs2d/build.d".  After a successful
conversion, csharptod.exe will use "rdmd" found in the PATH to run the build
script.  The build script will use "dmd" found in the PATH to compile and link.
This build script can also be run independtly like this:
```
> rdmd /SomePath/cs2d/build.d
```
Currently the generated build script only supports "dmd", but support for "ldc"
and "gdc" should be added.

How to Generate and Build the .NET Core Framework in D
--------------------------------------------------------------------------------
You can find latest source code and binaries of the generated core libraries at
https://github.com/marler8997/CSharpToDCoreclr.  Use the following instructions
to generate and build these files yourself:

1. Build csharptod
  - Open CSharpToD.sln, build
2. Build mscorlib
  - Download git repo https://github.com/dotnet/coreclr to the same directory as
    the CSharpToD repo.
  - Build mscorlib (generates some project files and makes sure that the project
    is valid).
  - Run `csharptod <CSharpToDRepo>/mscorlib/cs2d.config`
3. Build corefx
  - Download git repo https://github.com/dotnet/corefx to the same directory as
    the CSharpToD repo.
  - Build corefx (generates some project files and makes sure that the project
    is valid).
  - Run `csharptod <CSharpToDRepo>/corefx/cs2d.config`

Compatibility Over Performance
--------------------------------------------------------------------------------
CSharpToD prioritizes compatibility over performance. The idea is to make
CSharpToD work in as many cases as possible.  After that, optimizations in the
translation are considered.  If there is a performance mechanism that sacrifices
compatibility it can still be supported but will not be enabled by default.

One example of this is reflection.  If you know you're not going to need
reflection for a certain assembly, you could tell CSharpToD to omit the
reflection data structures.  This frees up some memory but also allows CSharpToD
to modify the type definitions because it does not have to maintain the original
structure for the sake of making reflection work properly.  This could mean not
having to use the `__DotNet__Object` and `__DotNet__Exception` types.
