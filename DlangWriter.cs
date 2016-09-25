using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToD
{
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

        public void Write(Stream stream)
        {
            sink.Put(stream);
        }

        unsafe void WriteLinePrefix()
        {
            byte* spaces = stackalloc byte[(int)prefixSpaceCount];
            StandardC.memset(spaces, ' ', (int)prefixSpaceCount);
            sink.Put(spaces, prefixSpaceCount);
            lineStarted = true;
        }

        public void HalfTab()
        {
            prefixSpaceCount += 2;
        }
        public void HalfUntab()
        {
            if (prefixSpaceCount == 0)
            {
                throw new InvalidOperationException("CodeBug: Untab called more than Tab");
            }
            prefixSpaceCount -= 2;
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



        uint WriteAnyPreviousLines(String str)
        {
            uint offset = 0;
            while (true)
            {
                int newlineIndex = str.IndexOf("\n", (int)offset);
                if (newlineIndex < 0)
                {
                    break;
                }

                if (!lineStarted)
                {
                    WriteLinePrefix();
                }
                uint stopAt = (uint)newlineIndex;
                if (stopAt > 0 && str[(int)stopAt - 1] == '\r')
                {
                    stopAt--;
                }
                sink.Put("//");
                sink.PutLine(str, offset, stopAt - offset);
                offset = (uint)newlineIndex + 1;
            }
            return offset;
        }
        public void WriteCommentedLine(String str)
        {
            uint offset = WriteAnyPreviousLines(str);

            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            sink.Put("//");
            sink.PutLine(str, offset, (uint)str.Length - offset);
            lineStarted = false;
        }
        public void WriteCommentedInline(String str)
        {
            uint offset = WriteAnyPreviousLines(str);

            if (!lineStarted)
            {
                WriteLinePrefix();
                lineStarted = true;
            }
            sink.Put("/*");
            sink.Put(str, offset, (uint)str.Length - offset);
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


        public void WriteLine()
        {
            sink.PutLine();
            lineStarted = false;
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


        static readonly Dictionary<string, string> PrimitiveSystemTypeMap = new Dictionary<string, string>
        {
            /*
            {"Byte"  , "ubyte" },
            {"SByte" , "byte" },
            {"Char"  , "wchar" },
            {"UInt16", "ushort" },
            {"Int16" , "short" },
            {"UInt32", "uint" },
            {"Int32" , "int" },
            {"UInt64", "ulong" },
            {"Int64" , "long" },
            */
            //{"Void", "void" },
            {"Object", "DotNetObject" },
            {"Exception", "DotNetException" },
        };
        static readonly Dictionary<string, string> PrimitiveSystemReflectionTypeMap = new Dictionary<string, string>
        {
            {"TypeInfo", "DotNetTypeInfo" },
        };
        public static string DotNetToD(TypeContext context, ITypeSymbol typeSymbol)
        {
            INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
            uint genericTypeCount = (namedTypeSymbol == null) ? 0 : (uint)namedTypeSymbol.Arity;
            return DotNetToD(context, typeSymbol.ContainingNamespace.Name, typeSymbol.Name, genericTypeCount);
        }
        public static string DotNetToD(TypeContext context, string @namespace, string typeName, UInt32 genericTypeCount)
        {
            if (genericTypeCount == 0)
            {
                if (@namespace == "System")
                {
                    if(context == TypeContext.Return && typeName == "Void")
                    {
                        return "void";
                    }

                    String dlangTypeName;
                    if (PrimitiveSystemTypeMap.TryGetValue(typeName, out dlangTypeName))
                    {
                        return dlangTypeName;
                    }
                }
                else if (@namespace == "System.Reflection")
                {
                    String dlangTypeName;
                    if (PrimitiveSystemReflectionTypeMap.TryGetValue(typeName, out dlangTypeName))
                    {
                        return dlangTypeName;
                    }
                }
            }
            return typeName;
        }
        public void WriteDlangTypeDeclName(String @namespace, String identifier, UInt32 genericTypeCount)
        {
            String dlangTypeName = DotNetToD(TypeContext.Default, @namespace, identifier, genericTypeCount);
            Write(dlangTypeName);
            if (genericTypeCount > 0)
            {
                Write("{0}", genericTypeCount);
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


        // TODO: This probably does't belong in the writer class
        public Boolean DlangTypeStringEqualsIdentifier(ITypeSymbol typeSymbol, String identifier)
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
        public void WriteDlangType(Stack<DeclContext> declContext, TypeContext context, ITypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind == TypeKind.Array)
            {
                WriteDlangType(declContext, context, ((IArrayTypeSymbol)typeSymbol).ElementType);
                Write("[]");
            }
            else if(typeSymbol.TypeKind == TypeKind.Pointer)
            {
                WriteDlangType(declContext, context, ((IPointerTypeSymbol)typeSymbol).PointedAtType);
                Write("*");
            }
            else
            {
                INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
                int genericTypeCount = 0;
                if (namedTypeSymbol != null)
                {
                    genericTypeCount = namedTypeSymbol.Arity;
                }

                if (typeSymbol.Kind != SymbolKind.TypeParameter)
                {
                    if (typeSymbol.ContainingType != null)
                    {
                        if (!declContext.Inside(typeSymbol.ContainingType))
                        {
                            WriteDlangType(declContext, context, typeSymbol.ContainingType);
                            Write(".");
                        }
                    }
                }

                String containingNamespace = (typeSymbol.ContainingNamespace == null) ?
                    "" : typeSymbol.ContainingNamespace.Name;

                String dlangTypeName = DotNetToD(context, containingNamespace, typeSymbol.Name, (uint)genericTypeCount);
                Write(dlangTypeName);
                if (genericTypeCount > 0)
                {
                    Write("{0}!(", namedTypeSymbol.Arity);
                    bool atFirst = true;
                    foreach (ITypeSymbol genericTypeArg in namedTypeSymbol.TypeArguments)
                    {
                        if (atFirst) { atFirst = false; } else { Write(","); }
                        WriteDlangType(declContext, context, genericTypeArg);
                    }
                    Write(")");
                }
            }
        }
    }
}
