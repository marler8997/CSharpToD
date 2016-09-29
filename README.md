
Setup
--------------------------------------------------------------------------------
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