﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ChangeSignature;
using Microsoft.CodeAnalysis.CSharp.CodeGeneration;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.LanguageServices;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Microsoft.CodeAnalysis.CSharp.ChangeSignature
{
    [ExportLanguageService(typeof(AbstractChangeSignatureService), LanguageNames.CSharp), Shared]
    internal sealed class CSharpChangeSignatureService : AbstractChangeSignatureService
    {
        protected override SyntaxGenerator Generator => CSharpSyntaxGenerator.Instance;
        protected override ISyntaxFacts SyntaxFacts => CSharpSyntaxFacts.Instance;

        private static readonly ImmutableArray<SyntaxKind> _declarationKinds = ImmutableArray.Create(
            SyntaxKind.MethodDeclaration,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.IndexerDeclaration,
            SyntaxKind.DelegateDeclaration,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.LocalFunctionStatement);

        private static readonly ImmutableArray<SyntaxKind> _declarationAndInvocableKinds =
            _declarationKinds.Concat(ImmutableArray.Create(
                SyntaxKind.InvocationExpression,
                SyntaxKind.ElementAccessExpression,
                SyntaxKind.ThisConstructorInitializer,
                SyntaxKind.BaseConstructorInitializer,
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.Attribute,
                SyntaxKind.NameMemberCref));

        private static readonly ImmutableArray<SyntaxKind> _updatableAncestorKinds = ImmutableArray.Create(
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.IndexerDeclaration,
            SyntaxKind.InvocationExpression,
            SyntaxKind.ElementAccessExpression,
            SyntaxKind.ThisConstructorInitializer,
            SyntaxKind.BaseConstructorInitializer,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.Attribute,
            SyntaxKind.DelegateDeclaration,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.NameMemberCref);

        private static readonly ImmutableArray<SyntaxKind> _updatableNodeKinds = ImmutableArray.Create(
            SyntaxKind.MethodDeclaration,
            SyntaxKind.LocalFunctionStatement,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.IndexerDeclaration,
            SyntaxKind.InvocationExpression,
            SyntaxKind.ElementAccessExpression,
            SyntaxKind.ThisConstructorInitializer,
            SyntaxKind.BaseConstructorInitializer,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.Attribute,
            SyntaxKind.DelegateDeclaration,
            SyntaxKind.NameMemberCref,
            SyntaxKind.AnonymousMethodExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.SimpleLambdaExpression);

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CSharpChangeSignatureService()
        {
        }

        public override async Task<(ISymbol symbol, int selectedIndex)> GetInvocationSymbolAsync(
            Document document, int position, bool restrictToDeclarations, CancellationToken cancellationToken)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var token = root.FindToken(position != tree.Length ? position : Math.Max(0, position - 1));

            // Allow the user to invoke Change-Sig if they've written:   Goo(a, b, c);$$ 
            if (token.Kind() == SyntaxKind.SemicolonToken && token.Parent is StatementSyntax)
            {
                token = token.GetPreviousToken();
                position = token.Span.End;
            }

            var matchingNode = GetMatchingNode(token.Parent, restrictToDeclarations);
            if (matchingNode == null)
            {
                return default;
            }

            // Don't show change-signature in the random whitespace/trivia for code.
            if (!matchingNode.Span.IntersectsWith(position))
            {
                return default;
            }

            // If we're actually on the declaration of some symbol, ensure that we're
            // in a good location for that symbol (i.e. not in the attributes/constraints).
            if (!InSymbolHeader(matchingNode, position))
            {
                return default;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var symbol = semanticModel.GetDeclaredSymbol(matchingNode, cancellationToken);
            if (symbol != null)
            {
                var selectedIndex = TryGetSelectedIndexFromDeclaration(position, matchingNode);
                return (symbol, selectedIndex);
            }

            if (matchingNode.IsKind(SyntaxKind.ObjectCreationExpression, out ObjectCreationExpressionSyntax objectCreation) &&
                token.Parent.AncestorsAndSelf().Any(a => a == objectCreation.Type))
            {
                var typeSymbol = semanticModel.GetSymbolInfo(objectCreation.Type, cancellationToken).Symbol;
                if (typeSymbol != null && typeSymbol.IsKind(SymbolKind.NamedType) && (typeSymbol as ITypeSymbol).TypeKind == TypeKind.Delegate)
                {
                    return (typeSymbol, 0);
                }
            }

            var symbolInfo = semanticModel.GetSymbolInfo(matchingNode, cancellationToken);
            return (symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault(), 0);
        }

        private static int TryGetSelectedIndexFromDeclaration(int position, SyntaxNode matchingNode)
        {
            var parameters = matchingNode.ChildNodes().OfType<BaseParameterListSyntax>().SingleOrDefault();
            return parameters != null ? GetParameterIndex(parameters.Parameters, position) : 0;
        }

        /// <summary>
        /// Find the position to insert the new parameter.
        /// We will insert a new comma and a parameter.
        /// </summary>
        protected override int GetPositionBeforeParameterListClosingBrace(SyntaxNode matchingNode)
        {
            var parameters = matchingNode.ChildNodes().OfType<BaseParameterListSyntax>().SingleOrDefault();
            return parameters switch
            {
                ParameterListSyntax parameterListSyntax => parameterListSyntax.CloseParenToken.SpanStart,
                BracketedParameterListSyntax bracketedParameterListSyntax => bracketedParameterListSyntax.CloseBracketToken.SpanStart,
                _ => throw new ArgumentException("Unexpected SyntaxNode", nameof(matchingNode))
            };
        }

        private SyntaxNode GetMatchingNode(SyntaxNode node, bool restrictToDeclarations)
        {
            var matchKinds = restrictToDeclarations
                ? _declarationKinds
                : _declarationAndInvocableKinds;

            for (var current = node; current != null; current = current.Parent)
            {
                if (restrictToDeclarations &&
                    current.Kind() == SyntaxKind.Block || current.Kind() == SyntaxKind.ArrowExpressionClause)
                {
                    return null;
                }

                if (matchKinds.Contains(current.Kind()))
                {
                    return current;
                }
            }

            return null;
        }

        private bool InSymbolHeader(SyntaxNode matchingNode, int position)
        {
            // Caret has to be after the attributes if the symbol has any.
            var lastAttributes = matchingNode.ChildNodes().LastOrDefault(n => n is AttributeListSyntax);
            var start = lastAttributes?.GetLastToken().GetNextToken().SpanStart ??
                        matchingNode.SpanStart;

            if (position < start)
            {
                return false;
            }

            // If the symbol has a parameter list, then the caret shouldn't be past the end of it.
            var parameterList = matchingNode.ChildNodes().LastOrDefault(n => n is ParameterListSyntax);
            if (parameterList != null)
            {
                return position <= parameterList.FullSpan.End;
            }

            // Case we haven't handled yet.  Just assume we're in the header.
            return true;
        }

        public override SyntaxNode FindNodeToUpdate(Document document, SyntaxNode node)
        {
            if (_updatableNodeKinds.Contains(node.Kind()))
            {
                return node;
            }

            // TODO: file bug about this: var invocation = csnode.Ancestors().FirstOrDefault(a => a.Kind == SyntaxKind.InvocationExpression);
            var matchingNode = node.AncestorsAndSelf().FirstOrDefault(n => _updatableAncestorKinds.Contains(n.Kind()));
            if (matchingNode == null)
            {
                return null;
            }

            var nodeContainingOriginal = GetNodeContainingTargetNode(matchingNode);
            if (nodeContainingOriginal == null)
            {
                return null;
            }

            return node.AncestorsAndSelf().Any(n => n == nodeContainingOriginal) ? matchingNode : null;
        }

        private SyntaxNode GetNodeContainingTargetNode(SyntaxNode matchingNode)
        {
            switch (matchingNode.Kind())
            {
                case SyntaxKind.InvocationExpression:
                    return (matchingNode as InvocationExpressionSyntax).Expression;

                case SyntaxKind.ElementAccessExpression:
                    return (matchingNode as ElementAccessExpressionSyntax).ArgumentList;

                case SyntaxKind.ObjectCreationExpression:
                    return (matchingNode as ObjectCreationExpressionSyntax).Type;

                case SyntaxKind.ConstructorDeclaration:
                case SyntaxKind.IndexerDeclaration:
                case SyntaxKind.ThisConstructorInitializer:
                case SyntaxKind.BaseConstructorInitializer:
                case SyntaxKind.Attribute:
                case SyntaxKind.DelegateDeclaration:
                case SyntaxKind.NameMemberCref:
                    return matchingNode;

                default:
                    return null;
            }
        }

        public override async Task<SyntaxNode> ChangeSignatureAsync(
            Document document,
            ISymbol declarationSymbol,
            SyntaxNode potentiallyUpdatedNode,
            SyntaxNode originalNode,
            SignatureChange signaturePermutation,
            CancellationToken cancellationToken)
        {
            var updatedNode = potentiallyUpdatedNode as CSharpSyntaxNode;

            // Update <param> tags.
            if (updatedNode.IsKind(SyntaxKind.MethodDeclaration) ||
                updatedNode.IsKind(SyntaxKind.ConstructorDeclaration) ||
                updatedNode.IsKind(SyntaxKind.IndexerDeclaration) ||
                updatedNode.IsKind(SyntaxKind.DelegateDeclaration))
            {
                var updatedLeadingTrivia = UpdateParamTagsInLeadingTrivia(document, updatedNode, declarationSymbol, signaturePermutation);
                if (updatedLeadingTrivia != default && !updatedLeadingTrivia.IsEmpty)
                {
                    updatedNode = updatedNode.WithLeadingTrivia(updatedLeadingTrivia);
                }
            }

            // Update declarations parameter lists
            if (updatedNode.IsKind(SyntaxKind.MethodDeclaration, out MethodDeclarationSyntax method))
            {
                var updatedParameters = UpdateDeclaration(method.ParameterList.Parameters, signaturePermutation, CreateNewParameterSyntax);
                return method.WithParameterList(method.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.LocalFunctionStatement, out LocalFunctionStatementSyntax localFunction))
            {
                var updatedParameters = UpdateDeclaration(localFunction.ParameterList.Parameters, signaturePermutation, CreateNewParameterSyntax);
                return localFunction.WithParameterList(localFunction.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ConstructorDeclaration, out ConstructorDeclarationSyntax constructor))
            {
                var updatedParameters = UpdateDeclaration(constructor.ParameterList.Parameters, signaturePermutation, CreateNewParameterSyntax);
                return constructor.WithParameterList(constructor.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.IndexerDeclaration, out IndexerDeclarationSyntax indexer))
            {
                var updatedParameters = UpdateDeclaration(indexer.ParameterList.Parameters, signaturePermutation, CreateNewParameterSyntax);
                return indexer.WithParameterList(indexer.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.DelegateDeclaration, out DelegateDeclarationSyntax delegateDeclaration))
            {
                var updatedParameters = UpdateDeclaration(delegateDeclaration.ParameterList.Parameters, signaturePermutation, CreateNewParameterSyntax);
                return delegateDeclaration.WithParameterList(delegateDeclaration.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.AnonymousMethodExpression, out AnonymousMethodExpressionSyntax anonymousMethod))
            {
                // Delegates may omit parameters in C#
                if (anonymousMethod.ParameterList == null)
                {
                    return anonymousMethod;
                }

                var updatedParameters = UpdateDeclaration(anonymousMethod.ParameterList.Parameters, signaturePermutation, CreateNewParameterSyntax);
                return anonymousMethod.WithParameterList(anonymousMethod.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.SimpleLambdaExpression, out SimpleLambdaExpressionSyntax lambda))
            {
                if (signaturePermutation.UpdatedConfiguration.ToListOfParameters().Any())
                {
                    var updatedParameters = UpdateDeclaration(SeparatedList<ParameterSyntax>(new[] { lambda.Parameter }), signaturePermutation, CreateNewParameterSyntax);
                    return ParenthesizedLambdaExpression(
                        lambda.AsyncKeyword,
                        ParameterList(updatedParameters),
                        lambda.ArrowToken,
                        lambda.Body);
                }
                else
                {
                    // No parameters. Change to a parenthesized lambda expression
                    var emptyParameterList = ParameterList()
                        .WithLeadingTrivia(lambda.Parameter.GetLeadingTrivia())
                        .WithTrailingTrivia(lambda.Parameter.GetTrailingTrivia());

                    return ParenthesizedLambdaExpression(lambda.AsyncKeyword, emptyParameterList, lambda.ArrowToken, lambda.Body);
                }
            }

            if (updatedNode.IsKind(SyntaxKind.ParenthesizedLambdaExpression, out ParenthesizedLambdaExpressionSyntax parenLambda))
            {
                var doNotSkipParameterType = parenLambda.ParameterList.Parameters.FirstOrDefault()?.Type != null;

                var updatedParameters = UpdateDeclaration(
                    parenLambda.ParameterList.Parameters,
                    signaturePermutation,
                    p => CreateNewParameterSyntax(p, !doNotSkipParameterType));
                return parenLambda.WithParameterList(parenLambda.ParameterList.WithParameters(updatedParameters));
            }

            // Update reference site argument lists
            if (updatedNode.IsKind(SyntaxKind.InvocationExpression, out InvocationExpressionSyntax invocation))
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var symbolInfo = semanticModel.GetSymbolInfo((InvocationExpressionSyntax)originalNode, cancellationToken);

                var isReducedExtensionMethod = symbolInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension };
                var isParamsArrayExpanded = IsParamsArrayExpanded(semanticModel, invocation, symbolInfo, cancellationToken);

                var numArguments = invocation.ArgumentList.Arguments.Count;
                var hasParamsArray = symbolInfo.Symbol.GetParameters().LastOrDefault()?.IsParams == true;
                var numParameters = symbolInfo.Symbol.GetParameters().Length;
                var isSomethingPassedToParamsArray = hasParamsArray && numArguments >= numParameters;

                var newArguments = PermuteArgumentList(declarationSymbol, invocation.ArgumentList.Arguments, signaturePermutation.WithoutAddedParameters(), isReducedExtensionMethod);
                newArguments = AddNewArgumentsToList(declarationSymbol, newArguments, signaturePermutation, isReducedExtensionMethod, isParamsArrayExpanded);
                return invocation.WithArgumentList(invocation.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ObjectCreationExpression, out ObjectCreationExpressionSyntax objCreation))
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var symbolInfo = semanticModel.GetSymbolInfo((ObjectCreationExpressionSyntax)originalNode, cancellationToken);

                var isParamsArrayExpanded = IsParamsArrayExpanded(semanticModel, objCreation, symbolInfo, cancellationToken);

                var newArguments = PermuteArgumentList(declarationSymbol, objCreation.ArgumentList.Arguments, signaturePermutation.WithoutAddedParameters());
                newArguments = AddNewArgumentsToList(declarationSymbol, newArguments, signaturePermutation, isReducedExtensionMethod: false, isParamsArrayExpanded);
                return objCreation.WithArgumentList(objCreation.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ThisConstructorInitializer, out ConstructorInitializerSyntax constructorInit) ||
                updatedNode.IsKind(SyntaxKind.BaseConstructorInitializer, out constructorInit))
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var symbolInfo = semanticModel.GetSymbolInfo((ConstructorInitializerSyntax)originalNode, cancellationToken);

                var isParamsArrayExpanded = IsParamsArrayExpanded(semanticModel, constructorInit, symbolInfo, cancellationToken);

                var newArguments = PermuteArgumentList(declarationSymbol, constructorInit.ArgumentList.Arguments, signaturePermutation.WithoutAddedParameters());
                newArguments = AddNewArgumentsToList(declarationSymbol, newArguments, signaturePermutation, isReducedExtensionMethod: false, isParamsArrayExpanded);
                return constructorInit.WithArgumentList(constructorInit.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ElementAccessExpression, out ElementAccessExpressionSyntax elementAccess))
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var symbolInfo = semanticModel.GetSymbolInfo((ElementAccessExpressionSyntax)originalNode, cancellationToken);

                var isParamsArrayExpanded = IsParamsArrayExpanded(semanticModel, elementAccess, symbolInfo, cancellationToken);

                var newArguments = PermuteArgumentList(declarationSymbol, elementAccess.ArgumentList.Arguments, signaturePermutation.WithoutAddedParameters());
                newArguments = AddNewArgumentsToList(declarationSymbol, newArguments, signaturePermutation, isReducedExtensionMethod: false, isParamsArrayExpanded);
                return elementAccess.WithArgumentList(elementAccess.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.Attribute, out AttributeSyntax attribute))
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var symbolInfo = semanticModel.GetSymbolInfo((AttributeSyntax)originalNode, cancellationToken);

                var isParamsArrayExpanded = IsParamsArrayExpanded(semanticModel, attribute, symbolInfo, cancellationToken);

                var newArguments = PermuteAttributeArgumentList(declarationSymbol, attribute.ArgumentList.Arguments, signaturePermutation.WithoutAddedParameters());
                newArguments = AddNewArgumentsToList(declarationSymbol, newArguments, signaturePermutation, isReducedExtensionMethod: false, isParamsArrayExpanded, generateAttributeArguments: true);
                return attribute.WithArgumentList(attribute.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            // Handle references in crefs
            if (updatedNode.IsKind(SyntaxKind.NameMemberCref, out NameMemberCrefSyntax nameMemberCref))
            {
                if (nameMemberCref.Parameters == null ||
                    !nameMemberCref.Parameters.Parameters.Any())
                {
                    return nameMemberCref;
                }

                var newParameters = UpdateDeclaration(nameMemberCref.Parameters.Parameters, signaturePermutation, CreateNewCrefParameterSyntax);

                var newCrefParameterList = nameMemberCref.Parameters.WithParameters(newParameters);
                return nameMemberCref.WithParameters(newCrefParameterList);
            }

            Debug.Assert(false, "Unknown reference location");
            return null;
        }

        private bool IsParamsArrayExpanded(SemanticModel semanticModel, SyntaxNode node, SymbolInfo symbolInfo, CancellationToken cancellationToken)
        {
            if (symbolInfo.Symbol == null)
            {
                return false;
            }

            int argumentCount;
            bool lastArgumentHasNameColon;
            ExpressionSyntax lastArgumentExpression;

            if (node is AttributeSyntax attribute)
            {
                argumentCount = attribute.ArgumentList.Arguments.Count;
                lastArgumentHasNameColon = attribute.ArgumentList.Arguments.LastOrDefault()?.NameColon != null ||
                    attribute.ArgumentList.Arguments.LastOrDefault()?.NameEquals != null;
                lastArgumentExpression = attribute.ArgumentList.Arguments.LastOrDefault()?.Expression;
            }
            else
            {
                BaseArgumentListSyntax argumentList = node switch
                {
                    InvocationExpressionSyntax invocation => invocation.ArgumentList,
                    ObjectCreationExpressionSyntax objectCreation => objectCreation.ArgumentList,
                    ConstructorInitializerSyntax constructorInitializer => constructorInitializer.ArgumentList,
                    ElementAccessExpressionSyntax elementAccess => elementAccess.ArgumentList,
                    _ => throw new ArgumentException("Unexpected SyntaxNode", nameof(node))
                };

                argumentCount = argumentList.Arguments.Count;
                lastArgumentHasNameColon = argumentList.Arguments.LastOrDefault()?.NameColon != null;
                lastArgumentExpression = argumentList.Arguments.LastOrDefault()?.Expression;
            }


            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.Parameters.LastOrDefault()?.IsParams == true)
            {
                if (argumentCount > ((IMethodSymbol)symbolInfo.Symbol).Parameters.Length)
                {
                    return true;
                }
                else if (argumentCount == ((IMethodSymbol)symbolInfo.Symbol).Parameters.Length)
                {
                    if (lastArgumentHasNameColon)
                    {
                        return true;
                    }
                    else
                    {
                        var fromType = semanticModel.GetTypeInfo(lastArgumentExpression, cancellationToken);
                        var toType = ((IMethodSymbol)symbolInfo.Symbol).Parameters.Last().Type;
                        return !semanticModel.Compilation.HasImplicitConversion(fromType.Type, toType);
                    }
                }
                else
                {
                    return false;
                }
            }

            return false;
        }

        private static ParameterSyntax CreateNewParameterSyntax(AddedParameter addedParameter)
            => CreateNewParameterSyntax(addedParameter, skipParameterType: false);

        private static ParameterSyntax CreateNewParameterSyntax(AddedParameter addedParameter, bool skipParameterType)
        {
            var equalsValueClause = addedParameter.HasDefaultValue
                ? EqualsValueClause(ParseExpression(addedParameter.DefaultValue))
                : null;

            return Parameter(
                attributeLists: default,
                modifiers: default,
                type: skipParameterType
                    ? null
                    : addedParameter.Type.GenerateTypeSyntax()
                        .WithTrailingTrivia(ElasticSpace),
                Identifier(addedParameter.Name),
                @default: equalsValueClause);
        }

        private static CrefParameterSyntax CreateNewCrefParameterSyntax(AddedParameter addedParameter)
            => CrefParameter(type: addedParameter.Type.GenerateTypeSyntax())
                .WithLeadingTrivia(ElasticSpace);

        private SeparatedSyntaxList<T> UpdateDeclaration<T>(
            SeparatedSyntaxList<T> list,
            SignatureChange updatedSignature,
            Func<AddedParameter, T> createNewParameterMethod) where T : SyntaxNode
        {
            var updatedDeclaration = base.UpdateDeclarationBase<T>(list, updatedSignature, createNewParameterMethod);
            return SeparatedList(updatedDeclaration.parameters, updatedDeclaration.separators);
        }

        protected override T TransferLeadingWhitespaceTrivia<T>(T newArgument, SyntaxNode oldArgument)
        {
            var oldTrivia = oldArgument.GetLeadingTrivia();
            var oldOnlyHasWhitespaceTrivia = oldTrivia.All(t => t.IsKind(SyntaxKind.WhitespaceTrivia));

            var newTrivia = newArgument.GetLeadingTrivia();
            var newOnlyHasWhitespaceTrivia = newTrivia.All(t => t.IsKind(SyntaxKind.WhitespaceTrivia));

            if (oldOnlyHasWhitespaceTrivia && newOnlyHasWhitespaceTrivia)
            {
                newArgument = newArgument.WithLeadingTrivia(oldTrivia);
            }

            return newArgument;
        }

        private SeparatedSyntaxList<AttributeArgumentSyntax> PermuteAttributeArgumentList(
            ISymbol declarationSymbol,
            SeparatedSyntaxList<AttributeArgumentSyntax> arguments,
            SignatureChange updatedSignature)
        {
            var newArguments = PermuteArguments(declarationSymbol, arguments.Select(a => UnifiedArgumentSyntax.Create(a)).ToImmutableArray(),
                updatedSignature);
            var numSeparatorsToSkip = arguments.Count - newArguments.Length;

            // copy whitespace trivia from original position
            var newArgumentsWithTrivia = TransferLeadingWhitespaceTrivia(
                newArguments.Select(a => (AttributeArgumentSyntax)(UnifiedArgumentSyntax)a), arguments);

            return SeparatedList(newArgumentsWithTrivia, GetSeparators(arguments, numSeparatorsToSkip));
        }

        private SeparatedSyntaxList<ArgumentSyntax> PermuteArgumentList(
            ISymbol declarationSymbol,
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            SignatureChange updatedSignature,
            bool isReducedExtensionMethod = false)
        {
            var newArguments = PermuteArguments(
                declarationSymbol,
                arguments.Select(a => UnifiedArgumentSyntax.Create(a)).ToImmutableArray(),
                updatedSignature,
                isReducedExtensionMethod);

            // copy whitespace trivia from original position
            var newArgumentsWithTrivia = TransferLeadingWhitespaceTrivia(
                newArguments.Select(a => (ArgumentSyntax)(UnifiedArgumentSyntax)a), arguments);

            var numSeparatorsToSkip = arguments.Count - newArguments.Length;
            return SeparatedList(newArgumentsWithTrivia, GetSeparators(arguments, numSeparatorsToSkip));
        }

        private ImmutableArray<T> TransferLeadingWhitespaceTrivia<T, U>(IEnumerable<T> newArguments, SeparatedSyntaxList<U> oldArguments)
            where T : SyntaxNode
            where U : SyntaxNode
        {
            var result = ImmutableArray.CreateBuilder<T>();
            var index = 0;
            foreach (var newArgument in newArguments)
            {
                result.Add(index < oldArguments.Count
                    ? TransferLeadingWhitespaceTrivia(newArgument, oldArguments[index])
                    : newArgument);

                index++;
            }

            return result.ToImmutable();
        }

        private ImmutableArray<SyntaxTrivia> UpdateParamTagsInLeadingTrivia(Document document, CSharpSyntaxNode node, ISymbol declarationSymbol, SignatureChange updatedSignature)
        {
            if (!node.HasLeadingTrivia)
            {
                return default;
            }

            var paramNodes = node
                .DescendantNodes(descendIntoTrivia: true)
                .OfType<XmlElementSyntax>()
                .Where(e => e.StartTag.Name.ToString() == DocumentationCommentXmlNames.ParameterElementName);

            var permutedParamNodes = VerifyAndPermuteParamNodes(paramNodes, declarationSymbol, updatedSignature);
            if (permutedParamNodes.IsEmpty)
            {
                return ImmutableArray<SyntaxTrivia>.Empty;
            }

            return GetPermutedDocCommentTrivia(document, node, permutedParamNodes);
        }

        private ImmutableArray<SyntaxNode> VerifyAndPermuteParamNodes(IEnumerable<XmlElementSyntax> paramNodes, ISymbol declarationSymbol, SignatureChange updatedSignature)
        {
            // Only reorder if count and order match originally.
            var originalParameters = updatedSignature.OriginalConfiguration.ToListOfParameters();
            var reorderedParameters = updatedSignature.UpdatedConfiguration.ToListOfParameters();

            var declaredParameters = declarationSymbol.GetParameters();
            if (paramNodes.Count() != declaredParameters.Length)
            {
                return ImmutableArray<SyntaxNode>.Empty;
            }

            // No parameters originally, so no param nodes to permute.
            if (declaredParameters.Length == 0)
            {
                return ImmutableArray<SyntaxNode>.Empty;
            }

            var dictionary = new Dictionary<string, XmlElementSyntax>();
            var i = 0;
            foreach (var paramNode in paramNodes)
            {
                var nameAttribute = paramNode.StartTag.Attributes.FirstOrDefault(a => a.Name.ToString().Equals("name", StringComparison.OrdinalIgnoreCase));
                if (nameAttribute == null)
                {
                    return ImmutableArray<SyntaxNode>.Empty;
                }

                var identifier = nameAttribute.DescendantNodes(descendIntoTrivia: true).OfType<IdentifierNameSyntax>().FirstOrDefault();
                if (identifier == null || identifier.ToString() != declaredParameters.ElementAt(i).Name)
                {
                    return ImmutableArray<SyntaxNode>.Empty;
                }

                dictionary.Add(originalParameters[i].Name.ToString(), paramNode);
                i++;
            }

            // Everything lines up, so permute them.
            var permutedParams = ArrayBuilder<SyntaxNode>.GetInstance();
            foreach (var parameter in reorderedParameters)
            {
                if (dictionary.TryGetValue(parameter.Name, out var permutedParam))
                {
                    permutedParams.Add(permutedParam);
                }
                else
                {
                    permutedParams.Add(XmlElement(
                        XmlElementStartTag(
                            XmlName("param"),
                            List<XmlAttributeSyntax>(new[] { XmlNameAttribute(parameter.Name) })),
                        XmlElementEndTag(XmlName("param"))));
                }
            }

            return permutedParams.ToImmutableAndFree();
        }

        public override async Task<ImmutableArray<SymbolAndProjectId>> DetermineCascadedSymbolsFromDelegateInvokeAsync(
            SymbolAndProjectId<IMethodSymbol> symbolAndProjectId,
            Document document,
            CancellationToken cancellationToken)
        {
            var symbol = symbolAndProjectId.Symbol;
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var nodes = root.DescendantNodes().ToImmutableArray();
            var convertedMethodGroups = nodes
                .WhereAsArray(
                    n =>
                    {
                        if (!n.IsKind(SyntaxKind.IdentifierName) ||
                            !semanticModel.GetMemberGroup(n, cancellationToken).Any())
                        {
                            return false;
                        }

                        ISymbol convertedType = semanticModel.GetTypeInfo(n, cancellationToken).ConvertedType;

                        if (convertedType != null)
                        {
                            convertedType = convertedType.OriginalDefinition;
                        }

                        if (convertedType != null)
                        {
                            convertedType = SymbolFinder.FindSourceDefinitionAsync(convertedType, document.Project.Solution, cancellationToken).WaitAndGetResult_CanCallOnBackground(cancellationToken) ?? convertedType;
                        }

                        return Equals(convertedType, symbol.ContainingType);
                    })
                .SelectAsArray(n => semanticModel.GetSymbolInfo(n, cancellationToken).Symbol);

            return convertedMethodGroups.SelectAsArray(symbolAndProjectId.WithSymbol);
        }

        protected override IEnumerable<AbstractFormattingRule> GetFormattingRules(Document document)
            => Formatter.GetDefaultFormattingRules(document).Concat(new ChangeSignatureFormattingRule());

        protected override SyntaxNode AddNameToArgument(SyntaxNode newArgument, string name)
        {
            return (newArgument as ArgumentSyntax).WithNameColon(NameColon(name));
        }

        protected override SyntaxNode CreateExplicitParamsArrayFromIndividualArguments(SeparatedSyntaxList<SyntaxNode> newArguments, int indexInExistingList, IParameterSymbol parameterSymbol)
        {
            var listOfArguments = SeparatedList(newArguments.Skip(indexInExistingList).Select(a => ((ArgumentSyntax)a).Expression), newArguments.GetSeparators().Skip(indexInExistingList));
            var initializerExpression = InitializerExpression(SyntaxKind.ArrayInitializerExpression, listOfArguments);
            var objectCreation = ArrayCreationExpression((ArrayTypeSyntax)parameterSymbol.Type.GenerateTypeSyntax(), initializerExpression);
            return Argument(objectCreation);
        }

        protected override bool SupportsOptionalAndParamsArrayParametersSimultaneously()
        {
            return true;
        }

        protected override SyntaxToken CommaTokenWithElasticSpace()
            => Token(SyntaxKind.CommaToken).WithTrailingTrivia(ElasticSpace);
    }
}
