using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToD
{
    class FirstPassVisitor : CSharpToDVisitor
    {
        readonly DlangWriter writer;

        readonly HashSet<ITypeSymbol> importTypesAlreadyAdded = new HashSet<ITypeSymbol>();
        public readonly KeyUniqueValues<string, TypeSymbolAndArity> importTypesByModule =
            new KeyUniqueValues<string, TypeSymbolAndArity>(true);

        readonly HashSet<ITypeSymbol> typesAlreadyAdded = new HashSet<ITypeSymbol>();
        // Key: the Type Name with the Generic Arity,
        // Value: list of modules that define the type used in this file
        public readonly KeyUniqueValues<TypeSymbolAndArity, string> modulesByTypeName =
            new KeyUniqueValues<TypeSymbolAndArity, string>(true);

        public readonly KeyValues<string, FileAndTypeDecl> partialTypes =
            new KeyValues<string, FileAndTypeDecl>(true);

        public FirstPassVisitor(DlangGenerator generator, DlangWriter writer)
            : base(generator)
        {
            this.writer = writer;
        }
        public override void DefaultVisit(SyntaxNode node)
        {
            throw new SyntaxNodeException(node, String.Format("FirstPassVisitor for '{0}' is not implemented", node.GetType().Name));
        }

        public override void VisitAttributeList(AttributeListSyntax attributeList)
        {
            AttributeAdder attributeAdder = null;
            if (attributeList.Target != null)
            {
                if (attributeList.Target.Identifier.Text == "assembly")
                {
                    attributeAdder = CSharpFileModelNodes.AddAssemblyAttribute;
                }
                else if (attributeList.Target.Identifier.Text == "module")
                {
                    attributeAdder = CSharpFileModelNodes.AddModuleAttribute;
                }
            }
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                AddNewType(true, "mscorlib.System", "__DotNet__Attribute", 0);
                AddNewType(true, "mscorlib.System", "__DotNet__AttributeStruct", 0);
                ITypeSymbol attributeType = currentFileModel.semanticModel.GetTypeInfo(attribute).Type;
                if (attributeType == null) throw new InvalidOperationException();
                AddTypeAndNamespace(attributeType);
                if(attributeAdder != null)
                {
                    attributeAdder(currentFileModelNodes, attribute);
                }
            }
        }
        void VisitAttributeLists(SyntaxList<AttributeListSyntax> attributeLists)
        {
            foreach (AttributeListSyntax attributeList in attributeLists)
            {
                VisitAttributeList(attributeList);
            }
        }

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            // TODO: add types for attributes
            foreach (var member in node.Members)
            {
                Visit(member);
            }
        }
        /*
        static string CombineModuleType(string module, string type)
        {
            if (String.IsNullOrEmpty(module))
            {
                return type;
            }
            if (String.IsNullOrEmpty(type))
            {
                return module;
            }
            return String.Format("{0}.{1}", module, type);
        }
        */
        void AddNewType(Boolean importTypeSymbol, String module, String dlangTypeIdentifier, UInt32 arity)
        {
            //String dModule = SemanticExtensions.NamespaceToDModule(@namespace);
            TypeSymbolAndArity typeNameAndArity = new TypeSymbolAndArity(dlangTypeIdentifier, arity);

            if (importTypeSymbol)
            {
                importTypesByModule.Add(module, typeNameAndArity);
            }
            modulesByTypeName.Add(typeNameAndArity, module);

            if (CSharpToD.generateDebug)
            {
                if (typeNameAndArity.arity == 0)
                {
                    writer.WriteLine("// Added type '{0}' (import {1}, module {2})",
                        typeNameAndArity.name, importTypeSymbol, module);
                }
                else
                {
                    writer.WriteLine("// Added type '{0}{1}' (import {2}, module {3})",
                        typeNameAndArity.name, typeNameAndArity.arity, importTypeSymbol, module);
                }
            }

            //Console.WriteLine("Namespace '{0}', from symbol '{1}'", typeSymbol.ContainingModule(), typeSymbol.Name);
            //if (log != null)
            //{
            //    log.PutLine(String.Format("Module '{0}', from symbol '{1}'", module, dlangTypeIdentifier));
            //}
        }
        void AddTypeAndNamespace(ITypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind == TypeKind.TypeParameter)
            {
                return;
            }
            else if (typeSymbol.TypeKind == TypeKind.Array)
            {
                AddTypeAndNamespace(((IArrayTypeSymbol)typeSymbol).ElementType);
            }
            else if (typeSymbol.TypeKind == TypeKind.Pointer)
            {
                AddTypeAndNamespace(((IPointerTypeSymbol)typeSymbol).PointedAtType);
            }
            else
            {
                bool importTypeSymbol;
                if (typeSymbol.ContainingType == null)
                {
                    importTypeSymbol = true;
                }
                else
                {
                    // do not import type symbol, because this symbol will need
                    // to be written as a dotted member of the parent type symbol
                    // ParentType.ThisType
                    // import ParentType;
                    importTypeSymbol = false;
                    AddTypeAndNamespace(typeSymbol.ContainingType);
                }

                //
                // Check if this type has already been added
                //
                if (importTypeSymbol)
                {
                    if (importTypesAlreadyAdded.Contains(typeSymbol))
                    {
                        Debug.Assert(typesAlreadyAdded.Contains(typeSymbol));
                        // if type was imported, it must have also been added
                        return;
                    }
                    importTypesAlreadyAdded.Add(typeSymbol);
                }
                else
                {
                    if (typesAlreadyAdded.Contains(typeSymbol))
                    {
                        return; // Type has already been added
                    }
                }
                typesAlreadyAdded.Add(typeSymbol);

                //
                // Add the type
                //
                INamedTypeSymbol namedTypeSymbol = typeSymbol as INamedTypeSymbol;
                uint arity = (namedTypeSymbol == null) ? 0 : (uint)namedTypeSymbol.Arity;
                if (String.IsNullOrEmpty(typeSymbol.Name))
                {
                    throw new InvalidOperationException();
                }

                String module = generator.GetModuleAndContainingType(typeSymbol);
                DType dType = SemanticExtensions.DotNetToD(TypeContext.Default,
                    module, typeSymbol.Name, arity);

                AddNewType(importTypeSymbol && !dType.isPrimitive, module, dType.name, arity);

                if (arity > 0)
                {
                    foreach (ITypeSymbol genericTypeArg in namedTypeSymbol.TypeArguments)
                    {
                        AddTypeAndNamespace(genericTypeArg);
                    }
                }
            }
        }

        void VisitTypeDeclaration(TypeDeclType typeDeclType, TypeDeclarationSyntax typeDecl)
        {
            if (TypeIsRemoved(typeDecl))
            {
                return;
            }

            VisitAttributeLists(typeDecl.AttributeLists);

            if (typeDecl.Modifiers.ContainsPartial())
            {
                partialTypes.Add(typeDecl.Identifier.Text,
                    new FileAndTypeDecl(currentFileModel, typeDecl));
            }

            AddTypeAndNamespace(currentFileModel.semanticModel.GetDeclaredSymbol(typeDecl));

            if (typeDecl.BaseList == null)
            {
                if (typeDeclType == TypeDeclType.Class)
                {
                    AddNewType(true, "mscorlib.System", "__DotNet__Object", 0);
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
                        AddNewType(true, "mscorlib.System", "__DotNet__Object", 0);
                    }
                }

                foreach (BaseTypeSyntax type in typeDecl.BaseList.Types)
                {
                    TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(type.Type);
                    if (typeInfo.Type == null)
                    {
                        throw new InvalidOperationException();
                    }
                    AddTypeAndNamespace(typeInfo.Type);
                }
            }

            if (CSharpToD.generateDebug)
            {
                writer.WriteLine("// Enter '{0}'", typeDecl.Identifier.Text);
                writer.Tab();
            }
            foreach (var member in typeDecl.Members)
            {
                Visit(member);
            }
            if (CSharpToD.generateDebug)
            {
                writer.Untab();
                writer.WriteLine("// Exit '{0}'", typeDecl.Identifier.Text);
            }

            if (typeDeclType == TypeDeclType.Struct)
            {
                if (typeDecl.BaseList != null)
                {
                    AddNewType(true, "mscorlib.System", "__DotNet__Object", 0);
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
            VisitAttributeLists(delegateDecl.AttributeLists);
            AddTypeAndNamespace(currentFileModel.semanticModel.GetTypeInfo(delegateDecl.ReturnType).Type);
            foreach (ParameterSyntax param in delegateDecl.ParameterList.Parameters)
            {
                //VisitAttributeLists(param.AttributeLists);
                AddTypeAndNamespace(currentFileModel.semanticModel.GetTypeInfo(param.Type).Type);
            }
        }
        public override void VisitEnumDeclaration(EnumDeclarationSyntax enumDecl)
        {
            VisitAttributeLists(enumDecl.AttributeLists);
            AddTypeAndNamespace(currentFileModel.semanticModel.GetDeclaredSymbol(enumDecl));
            foreach (EnumMemberDeclarationSyntax enumMember in enumDecl.Members)
            {
                // TODO: generate reflection from attributes
                //VisitAttributeLists(enumMember.AttributeLists);
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
            VisitAttributeLists(fieldDecl.AttributeLists);
            ITypeSymbol fieldType = currentFileModel.semanticModel.GetTypeInfo(fieldDecl.Declaration.Type).Type;
            if (CSharpToD.generateDebug)
            {
                writer.WriteCommentedLine(String.Format(
                    "FirstPass: VisitField Type={0}", fieldDecl.Declaration.Type.GetText().ToString().Trim()));
            }
            AddTypeAndNamespace(fieldType);
            if (!CSharpToD.skeleton)
            {
                foreach (VariableDeclaratorSyntax variableDecl in fieldDecl.Declaration.Variables)
                {
                    if (variableDecl.ArgumentList != null)
                    {
                        Visit(variableDecl.ArgumentList);
                    }
                    Visit(variableDecl.Initializer);
                }
            }
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
        // Simple Syntax
        //
        public override void VisitGenericName(GenericNameSyntax genericName)
        {
            // TODO: generic name may not be referring to a type, it could be a method.
            //       figure out how to handle this.
            AddTypeAndNamespaceFromSyntax(genericName, false);
        }
        public override void VisitEqualsValueClause(EqualsValueClauseSyntax equalsValueClause)
        {
            Visit(equalsValueClause.Value);
        }
        public override void VisitIdentifierName(IdentifierNameSyntax identifier)
        {
            TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(identifier);
            if (typeInfo.Type != null)
            {
                // TODO: I will need the type namespace, but maybe not the type symbol?
                AddTypeAndNamespace(typeInfo.Type);
            }
        }
        public override void VisitParameter(ParameterSyntax parameter)
        {
            VisitAttributeLists(parameter.AttributeLists);
            if (parameter.Type != null)
            {
                AddTypeAndNamespaceFromSyntax(parameter.Type, false);
            }
            Visit(parameter.Default);
        }
        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax lambdaExpression)
        {
            Visit(lambdaExpression.Parameter);
            Visit(lambdaExpression.Body);
        }
        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax lambdaExpression)
        {
            Visit(lambdaExpression.ParameterList);
            Visit(lambdaExpression.Body);
        }
        public override void VisitBracketedArgumentList(BracketedArgumentListSyntax argumentList)
        {
            foreach (ArgumentSyntax argument in argumentList.Arguments)
            {
                Visit(argument);
            }
        }


        //
        // Expressions
        //
        public override void VisitParenthesizedExpression(ParenthesizedExpressionSyntax parensExpression)
        {
            Visit(parensExpression.Expression);
        }
        public override void VisitAssignmentExpression(AssignmentExpressionSyntax assignmentExpression)
        {
            Visit(assignmentExpression.Left);
            Visit(assignmentExpression.Right);
        }
        public override void VisitLiteralExpression(LiteralExpressionSyntax literalExpression)
        {
            // I think literals do not contain types...not 100% sure though
        }
        public override void VisitInitializerExpression(InitializerExpressionSyntax initializerExpression)
        {
            foreach (ExpressionSyntax expression in initializerExpression.Expressions)
            {
                Visit(expression);
            }
        }
        public override void VisitBinaryExpression(BinaryExpressionSyntax binaryExpression)
        {
            Visit(binaryExpression.Left);
            Visit(binaryExpression.Right);
        }
        public override void VisitCastExpression(CastExpressionSyntax cast)
        {
            AddTypeAndNamespace(currentFileModel.semanticModel.GetTypeInfo(cast.Type).Type);
            Visit(cast.Expression);
        }
        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax memberAccess)
        {
            Visit(memberAccess.Expression);
            TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(memberAccess.Name);
            if (typeInfo.Type != null)
            {
                // TODO: I will need the type namespace, but maybe not the type symbol?
                AddTypeAndNamespace(typeInfo.Type);
            }
        }


        void AddTypeAndNamespaceFromSyntax(TypeSyntax typeSyntax, bool convertedType)
        {
            if (convertedType)
            {
                throw new NotImplementedException();
            }

            {
                TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(typeSyntax);
                ITypeSymbol typeSymbol = typeInfo.Type;
                ITypeSymbol convertedTypeSymbol = typeInfo.ConvertedType;
                if (typeSymbol != null)
                {
                    AddTypeAndNamespace(typeSymbol);
                    return;
                }
            }

            // the type syntax was not a type
            SymbolInfo symbolInfo = currentFileModel.semanticModel.GetSymbolInfo(typeSyntax);
            if (symbolInfo.Symbol == null)
            {
                throw new InvalidOperationException();
            }
            if (CSharpToD.generateDebug)
            {
                writer.Write("// '{0}' is not a type, it is a(n) '{1}'",
                    typeSyntax.GetText().ToString().Trim().Replace("\n", "").Replace("\r\n",""),
                    symbolInfo.Symbol.Kind);
            }
        }

        public override void VisitCheckedExpression(CheckedExpressionSyntax checkedExpression)
        {
            Visit(checkedExpression.Expression);
        }
        public override void VisitTypeOfExpression(TypeOfExpressionSyntax typeofExpression)
        {
            AddTypeAndNamespaceFromSyntax(typeofExpression.Type, false);
        }
        public override void VisitDefaultExpression(DefaultExpressionSyntax defaultExpression)
        {
            AddTypeAndNamespaceFromSyntax(defaultExpression.Type, false);
        }
        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax unaryExpression)
        {
            Visit(unaryExpression.Operand);
        }
        public override void VisitConditionalExpression(ConditionalExpressionSyntax conditionalExpression)
        {
            Visit(conditionalExpression.Condition);
            Visit(conditionalExpression.WhenTrue);
            Visit(conditionalExpression.WhenFalse);
        }
        public override void VisitInvocationExpression(InvocationExpressionSyntax invocationExpression)
        {
            // TODO: the expression is a method...figure out how to handle this
            Visit(invocationExpression.Expression);
            foreach (ArgumentSyntax argumentSyntax in invocationExpression.ArgumentList.Arguments)
            {
                Visit(argumentSyntax);
            }
        }
        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax objectCreationExpression)
        {
            if (objectCreationExpression.Initializer != null)
            {
                throw new InvalidOperationException(String.Format(
                    "ObjectCreationExpression.Initializer is not null? \"{0}\"",
                    objectCreationExpression.Initializer.GetText().ToString().Trim()));
            }

            AddTypeAndNamespaceFromSyntax(objectCreationExpression.Type, false);
            Visit(objectCreationExpression.ArgumentList);
        }
        public override void VisitArrayCreationExpression(ArrayCreationExpressionSyntax newArrayExpression)
        {
            Visit(newArrayExpression.Type);
            Visit(newArrayExpression.Initializer);
        }
        public override void VisitArrayType(ArrayTypeSyntax arrayTypeSyntax)
        {
            Visit(arrayTypeSyntax.ElementType);
            foreach (ArrayRankSpecifierSyntax rankSpecifier in arrayTypeSyntax.RankSpecifiers)
            {
                foreach (ExpressionSyntax rankSpecifierSize in rankSpecifier.Sizes)
                {
                    Visit(rankSpecifierSize);
                }
            }
        }
        public override void VisitPredefinedType(PredefinedTypeSyntax predefinedType)
        {
            TypeInfo typeInfo = currentFileModel.semanticModel.GetTypeInfo(predefinedType);
            if (typeInfo.Type == null)
            {
                throw new InvalidOperationException();
            }
            AddTypeAndNamespace(typeInfo.Type);
        }
        public override void VisitArgumentList(ArgumentListSyntax node)
        {
            foreach (ArgumentSyntax argumentSyntax in node.Arguments)
            {
                Visit(argumentSyntax);
            }
        }
        public override void VisitArgument(ArgumentSyntax argumentSyntax)
        {
            Visit(argumentSyntax.Expression);
        }
        public override void VisitOmittedArraySizeExpression(OmittedArraySizeExpressionSyntax node)
        {
            if (node.OmittedArraySizeExpressionToken.Text.Length != 0)
            {
                throw new NotImplementedException(String.Format("OmittedArraySizeExpressionSyntax \"{0}\"",
                    node.OmittedArraySizeExpressionToken.Text));
            }
        }

        public override void VisitParameterList(ParameterListSyntax parameterList)
        {
            foreach (ParameterSyntax parameterSyntax in parameterList.Parameters)
            {
                VisitParameter(parameterSyntax);
            }
        }

        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax anonymousMethod)
        {
            if (anonymousMethod.ParameterList != null)
            {
                VisitParameterList(anonymousMethod.ParameterList);
            }
            Visit(anonymousMethod.Body);
        }

        public override void VisitBlock(BlockSyntax blockSyntax)
        {
            if (!CSharpToD.skeleton)
            {
                // TODO: could blockSyntax.Statements be null?
                foreach (StatementSyntax statement in blockSyntax.Statements)
                {
                    Visit(statement);
                }
            }
        }

        //
        // Statements
        //
        public override void VisitExpressionStatement(ExpressionStatementSyntax expressionStatement)
        {
            Visit(expressionStatement.Expression);
        }
        public override void VisitReturnStatement(ReturnStatementSyntax returnStatement)
        {
            Visit(returnStatement.Expression);
        }
        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            VisitVariableDeclaration(node.Declaration);
        }
        public override void VisitVariableDeclaration(VariableDeclarationSyntax variableDecl)
        {
            AddTypeAndNamespaceFromSyntax(variableDecl.Type, false);
            foreach (VariableDeclaratorSyntax variableDeclarator in variableDecl.Variables)
            {
                if (variableDeclarator.ArgumentList != null)
                {
                    Visit(variableDeclarator.ArgumentList);
                }
                Visit(variableDeclarator.Initializer);
            }
        }
        public override void VisitIfStatement(IfStatementSyntax ifStatement)
        {
            Visit(ifStatement.Condition);
            Visit(ifStatement.Statement);
            Visit(ifStatement.Else);
        }
    }
}
