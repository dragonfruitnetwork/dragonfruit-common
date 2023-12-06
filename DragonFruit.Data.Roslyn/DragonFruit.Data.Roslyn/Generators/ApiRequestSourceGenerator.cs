﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using DragonFruit.Data.Requests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Scriban;

namespace DragonFruit.Data.Roslyn.Generators
{
    [Generator(LanguageNames.CSharp)]
    public class ApiRequestSourceGenerator : IIncrementalGenerator
    {
        private static readonly Template RequestTemplate;

        private static readonly HashSet<SpecialType> SupportedCollectionTypes = new(new[]
        {
            SpecialType.System_Array,
            SpecialType.System_Collections_IEnumerable,
            SpecialType.System_Collections_Generic_IList_T,
            SpecialType.System_Collections_Generic_ICollection_T,
            SpecialType.System_Collections_Generic_IEnumerable_T,
            SpecialType.System_Collections_Generic_IReadOnlyList_T,
            SpecialType.System_Collections_Generic_IReadOnlyCollection_T,
        });

        static ApiRequestSourceGenerator()
        {
            using var templateStream = typeof(ApiRequestSourceGenerator).Assembly.GetManifestResourceStream("DragonFruit.Data.Roslyn.Generators.Templates.ApiRequest.liquid");

            if (templateStream == null)
            {
                throw new NullReferenceException("Could not find template");
            }

            using var reader = new System.IO.StreamReader(templateStream);
            RequestTemplate = Template.ParseLiquid(reader.ReadToEnd());
        }

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var apiRequestDerivedClasses = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => syntaxNode is ClassDeclarationSyntax classDecl && classDecl.Modifiers.Any(x => x.IsKind(SyntaxKind.PartialKeyword)),
                transform: (generatorSyntaxContext, _) => GetSemanticTarget(generatorSyntaxContext));

            IncrementalValueProvider<(Compilation, ImmutableArray<ClassDeclarationSyntax>)> targets = context.CompilationProvider.Combine(apiRequestDerivedClasses.Collect());
            context.RegisterSourceOutput(targets, static (spc, source) => Execute(source.Item1, source.Item2, spc));
        }

        private static ClassDeclarationSyntax GetSemanticTarget(GeneratorSyntaxContext context)
        {
            var model = context.SemanticModel;
            var classDeclaration = (ClassDeclarationSyntax)context.Node;

            var classSymbol = ModelExtensions.GetDeclaredSymbol(model, classDeclaration) as INamedTypeSymbol;

            // ensure the class isn't abstract
            if (classSymbol?.IsAbstract != false)
            {
                return null;
            }

            while (classSymbol != null)
            {
                if (classSymbol.ToString() == "DragonFruit.Data.ApiRequest")
                {
                    return classDeclaration;
                }

                classSymbol = classSymbol.BaseType;
            }

            return null;
        }

        private static void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> requestClasses, SourceProductionContext context)
        {
            if (requestClasses.IsDefaultOrEmpty)
            {
                return;
            }

            foreach (var classDeclaration in requestClasses.Distinct())
            {
                var model = compilation.GetSemanticModel(classDeclaration.SyntaxTree, true);
                var classSymbol = model.GetDeclaredSymbol(classDeclaration)!;

                var sourceBuilder = new StringBuilder("// <auto-generated />");
                var parameters = GetRequestSymbolMetadata(compilation, classSymbol);

                var parameterInfo = new
                {
                    Namespace = classSymbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ClassName = classSymbol.Name,
                    HeaderParameters = parameters[ParameterType.Header],
                    QueryParameters = parameters[ParameterType.Query],

                    // todo add form body content
                };

                sourceBuilder.Append("\n\n");
                sourceBuilder.Append(RequestTemplate.Render(parameterInfo));
                context.AddSource($"{classSymbol.Name}.g.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));
            }
        }

        private static IReadOnlyDictionary<ParameterType, IList<RequestSymbolMetadata>> GetRequestSymbolMetadata(Compilation compilation, INamedTypeSymbol symbol)
        {
            var symbols = Enum.GetValues(typeof(ParameterType)).Cast<ParameterType>().ToDictionary(x => x, _ => (IList<RequestSymbolMetadata>)new List<RequestSymbolMetadata>());

            // get types used in member processing
            var enumerableParameterAttribute = compilation.GetTypeByMetadataName(typeof(EnumerableOptionsAttribute).FullName);
            var requestParameterAttribute = compilation.GetTypeByMetadataName(typeof(RequestParameterAttribute).FullName);
            var enumParameterAttribute = compilation.GetTypeByMetadataName(typeof(EnumOptionsAttribute).FullName);

            var enumerableTypeSymbol = compilation.GetTypeByMetadataName(typeof(IEnumerable).FullName);
            var apiRequestBaseType = compilation.GetTypeByMetadataName(typeof(ApiRequest).FullName);

            // track properties already visited
            var depth = 0;
            var currentSymbol = symbol;
            var consumedProperties = new HashSet<string>();

            do
            {
                foreach (var candidate in currentSymbol.GetMembers().Where(x => x is IPropertySymbol or IMethodSymbol { Parameters.Length: 0 }))
                {
                    var requestAttribute = candidate.GetAttributes().SingleOrDefault(x => x.AttributeClass?.Equals(requestParameterAttribute, SymbolEqualityComparer.Default) == true);

                    // ensure that properties ovewritten using "new" are not processed twice
                    if (requestAttribute == null || !consumedProperties.Add(candidate.MetadataName))
                    {
                        continue;
                    }

                    var returnType = candidate switch
                    {
                        IPropertySymbol propertySymbol => propertySymbol.Type,
                        IMethodSymbol methodSymbol => methodSymbol.ReturnType,

                        _ => throw new NotSupportedException()
                    };

                    if (returnType.SpecialType == SpecialType.System_Void)
                    {
                        // todo return diagnostic warning
                        continue;
                    }

                    var parameterType = (ParameterType)requestAttribute.ConstructorArguments[0].Value!;
                    var parameterName = (string)requestAttribute.ConstructorArguments.ElementAtOrDefault(1).Value ?? candidate.Name;

                    RequestSymbolMetadata metadata;

                    // handle enums
                    if (returnType.TypeKind == TypeKind.Enum)
                    {
                        var enumOptions = candidate.GetAttributes().SingleOrDefault(x => x.AttributeClass?.Equals(enumParameterAttribute, SymbolEqualityComparer.Default) == true);

                        metadata = new EnumRequestSymbolMetadata
                        {
                            EnumOption = (EnumOption?)enumOptions?.ConstructorArguments.ElementAt(0).Value ?? EnumOption.None
                        };
                    }
                    // handle arrays/IEnumerable
                    else if (SupportedCollectionTypes.Contains(returnType.SpecialType) || returnType.FindImplementationForInterfaceMember(enumerableTypeSymbol) != null)
                    {
                        var enumerableOptions = candidate.GetAttributes().SingleOrDefault(x => x.AttributeClass?.Equals(enumerableParameterAttribute, SymbolEqualityComparer.Default) == true);

                        metadata = new EnumerableRequestSymbolMetadata
                        {
                            Separator = (string)enumerableOptions?.ConstructorArguments.ElementAtOrDefault(1).Value ?? ",",
                            EnumerableOption = (EnumerableOption?)enumerableOptions?.ConstructorArguments.ElementAt(0).Value ?? EnumerableOption.Concatenated
                        };
                    }
                    else
                    {
                        metadata = new RequestSymbolMetadata
                        {
                            IsString = returnType.SpecialType == SpecialType.System_String
                        };
                    }

                    metadata.Depth = depth;
                    metadata.Symbol = candidate;
                    metadata.ParameterName = parameterName;
                    metadata.Nullable = returnType.IsReferenceType || (returnType.IsValueType && returnType.NullableAnnotation == NullableAnnotation.Annotated);

                    symbols[parameterType].Add(metadata);
                }

                // get derived class from currentSymbol
                currentSymbol = currentSymbol.BaseType;
                depth++;
            } while (currentSymbol?.Equals(apiRequestBaseType, SymbolEqualityComparer.Default) == false);

            return symbols;
        }
    }
}
