using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using NUnit.Analyzers.Constants;
using NUnit.Analyzers.Extensions;
using NUnit.Analyzers.Helpers;
using NUnit.Analyzers.Operations;

namespace NUnit.Analyzers.ComparableTypes
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class ComparableTypesAnalyzer : BaseAssertionAnalyzer
    {
        private static readonly ImmutableHashSet<string> SupportedConstraints = ImmutableHashSet.Create(
            NunitFrameworkConstants.NameOfIsLessThan,
            NunitFrameworkConstants.NameOfIsLessThanOrEqualTo,
            NunitFrameworkConstants.NameOfIsGreaterThan,
            NunitFrameworkConstants.NameOfIsGreaterThanOrEqualTo);

        private static readonly DiagnosticDescriptor descriptor = DiagnosticDescriptorCreator.Create(
            id: AnalyzerIdentifiers.ComparableTypes,
            title: ComparableTypesConstants.Title,
            messageFormat: ComparableTypesConstants.Message,
            category: Categories.Assertion,
            defaultSeverity: DiagnosticSeverity.Error,
            description: ComparableTypesConstants.Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(descriptor);

        protected override void AnalyzeAssertInvocation(OperationAnalysisContext context, IInvocationOperation assertOperation)
        {
            if (!AssertHelper.TryGetActualAndConstraintOperations(assertOperation,
                out var actualOperation, out var constraintExpression))
            {
                return;
            }

            if (actualOperation == null)
                return;

            var actualType = AssertHelper.GetUnwrappedActualType(actualOperation);

            if (actualType == null)
                return;

            foreach (var constraintPartExpression in constraintExpression.ConstraintParts)
            {
                if (HasIncompatiblePrefixes(constraintPartExpression)
                    || HasCustomComparer(constraintPartExpression)
                    || constraintPartExpression.HasUnknownExpressions())
                {
                    continue;
                }

                var constraintMethod = constraintPartExpression.GetConstraintMethod();
                if (constraintMethod == null)
                    continue;

                if (!SupportedConstraints.Contains(constraintMethod.Name))
                    continue;

                var expectedOperation = constraintPartExpression.GetExpectedArgument();
                if (expectedOperation == null)
                    continue;

                var expectedType = expectedOperation.Type;
                if (expectedType == null)
                    continue;

                if (!CanCompare(actualType, expectedType, context.Compilation))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        descriptor,
                        expectedOperation.Syntax.GetLocation(),
                        constraintMethod.Name));
                }
            }
        }

        private static bool CanCompare(ITypeSymbol actualType, ITypeSymbol expectedType, Compilation compilation)
        {
            if (actualType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                actualType = ((INamedTypeSymbol)actualType).TypeArguments[0];

            if (expectedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                expectedType = ((INamedTypeSymbol)expectedType).TypeArguments[0];

            var conversion = compilation.ClassifyConversion(actualType, expectedType);
            if (conversion.IsNumeric)
                return true;

            if (IsIComparable(actualType, expectedType) || IsIComparable(expectedType, actualType))
                return true;

            // NUnit doesn't demand that IComparable is for the same type.
            // But MS does: https://docs.microsoft.com/en-us/dotnet/api/system.icomparable.compareto?view=netcore-3.1
            if (actualType == expectedType && IsIComparable(actualType))
                return true;

            return false;
        }

        private static bool IsIComparable(ITypeSymbol typeSymbol, ITypeSymbol comparableTypeArguments)
        {
            if (typeSymbol.AllInterfaces.Any(i => i.TypeArguments.Length == 1
                && i.TypeArguments[0].Equals(comparableTypeArguments)
                && i.GetFullMetadataName() == "System.IComparable`1"))
            {
                return true;
            }

            // NUnit allows for an CompareTo method, even if not implementing IComparable.
            return typeSymbol.GetAllMembers().Any(x => x is IMethodSymbol methodSymbol && methodSymbol.Name == "CompareTo"
                && methodSymbol.Parameters.Length == 1 && methodSymbol.Parameters[0].Type.Equals(comparableTypeArguments));
        }

        private static bool IsIComparable(ITypeSymbol typeSymbol)
        {
            return typeSymbol.AllInterfaces.Any(i => i.TypeArguments.Length == 0
                && i.GetFullMetadataName() == "System.IComparable");
        }

        private static bool HasIncompatiblePrefixes(ConstraintExpressionPart constraintPartExpression)
        {
            // Currently only 'Not' suffix supported, as all other suffixes change actual type for constraint
            // (e.g. All, Some, Property, Count, etc.)

            return constraintPartExpression.GetPrefixesNames().Any(s => s != NunitFrameworkConstants.NameOfIsNot);
        }

        private static bool HasCustomComparer(ConstraintExpressionPart constraintPartExpression)
        {
            return constraintPartExpression.GetSuffixesNames().Any(s => s == NunitFrameworkConstants.NameOfUsing);
        }
    }
}