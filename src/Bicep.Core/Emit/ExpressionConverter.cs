﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using Arm.Expression.Expressions;
using Bicep.Core.SemanticModel;
using Bicep.Core.Syntax;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bicep.Core.Emit
{
    public class ExpressionConverter
    {
        private readonly SemanticModel.SemanticModel model;

        public ExpressionConverter(SemanticModel.SemanticModel model)
        {
            this.model = model;
        }

        /// <summary>
        /// Converts the specified bicep expression tree into an ARM template expression tree.
        /// The returned tree may be rooted at either a function expression or jtoken expression.
        /// </summary>
        /// <param name="expression">The expression</param>
        public LanguageExpression ConvertExpression(SyntaxBase expression)
        {
            switch (expression)
            {
                case BooleanLiteralSyntax boolSyntax:
                    return CreateJsonFunctionCall(boolSyntax.Value);
                    
                case NumericLiteralSyntax numericSyntax:
                    return new JTokenExpression(numericSyntax.Value);

                case StringSyntax stringSyntax:
                    // using the throwing method to get semantic value of the string because
                    // error checking should have caught any errors by now
                    return ConvertString(stringSyntax);
                    
                case NullLiteralSyntax _:
                    return CreateJsonFunctionCall(JValue.CreateNull());

                case ObjectSyntax _:
                case ArraySyntax _:
                    return ConvertComplexLiteral(expression);

                case ParenthesizedExpressionSyntax parenthesized:
                    // template expressions do not have operators so parentheses are irrelevant
                    return ConvertExpression(parenthesized.Expression);

                case UnaryOperationSyntax unary:
                    return ConvertUnary(unary);

                case BinaryOperationSyntax binary:
                    return ConvertBinary(binary);

                case TernaryOperationSyntax ternary:
                    return new FunctionExpression(
                        "if",
                        new[]
                        {
                            ConvertExpression(ternary.ConditionExpression),
                            ConvertExpression(ternary.TrueExpression),
                            ConvertExpression(ternary.FalseExpression)
                        },
                        Array.Empty<LanguageExpression>());

                case FunctionCallSyntax function:
                    return ConvertFunction(
                        function.FunctionName.IdentifierName,
                        function.Arguments.Select(a => ConvertExpression(a.Expression)).ToArray());

                case ArrayAccessSyntax arrayAccess:
                    return AppendProperty(
                        ToFunctionExpression(arrayAccess.BaseExpression),
                        ConvertExpression(arrayAccess.IndexExpression));

                case PropertyAccessSyntax propertyAccess:
                    return AppendProperty(
                        ToFunctionExpression(propertyAccess.BaseExpression),
                        new JTokenExpression(propertyAccess.PropertyName.IdentifierName));

                case VariableAccessSyntax variableAccess:
                    return ConvertVariableAccess(variableAccess, model);

                default:
                    throw new NotImplementedException($"Cannot emit unexpected expression of type {expression.GetType().Name}");
            }
        }

        private static LanguageExpression ConvertVariableAccess(VariableAccessSyntax variableAccessSyntax, SemanticModel.SemanticModel model)
        {
            string name = variableAccessSyntax.Name.IdentifierName;

            var symbol = model.GetSymbolInfo(variableAccessSyntax);

            // TODO: This will change to support inlined functions like reference() or list*()
            switch (symbol)
            {
                case ParameterSymbol _:
                    return CreateUnaryFunction("parameters", new JTokenExpression(name));

                case VariableSymbol _:
                    return CreateUnaryFunction("variables", new JTokenExpression(name));

                default:
                    throw new NotImplementedException($"Encountered an unexpected symbol kind '{symbol?.Kind}' when generating a variable access expression.");
            }
        }

        private LanguageExpression ConvertString(StringSyntax syntax)
        {
            if (!syntax.IsInterpolated())
            {
                // no need to build a format string
                return new JTokenExpression(syntax.GetLiteralValue());;
            }

            var formatArgs = new LanguageExpression[syntax.Expressions.Length + 1];

            var formatString = StringFormatConverter.BuildFormatString(syntax);
            formatArgs[0] = new JTokenExpression(formatString);

            for (var i = 0; i < syntax.Expressions.Length; i++)
            {
                formatArgs[i + 1] = ConvertExpression(syntax.Expressions[i]);
            }

            return new FunctionExpression("format", formatArgs, Array.Empty<LanguageExpression>());
        }

        /// <summary>
        /// Converts the specified bicep expression tree into an ARM template expression tree.
        /// This always returns a function expression, which is useful when converting property access or array access
        /// on literals.
        /// </summary>
        /// <param name="expression">The expression</param>
        public FunctionExpression ToFunctionExpression(SyntaxBase expression)
        {
            var converted = ConvertExpression(expression);
            switch (converted)
            {
                case FunctionExpression functionExpression:
                    return functionExpression;

                case JTokenExpression valueExpression:
                    JToken value = valueExpression.Value;

                    switch (value.Type)
                    {
                        case JTokenType.Integer:
                            // convert integer literal to a function call via int() function
                            return CreateUnaryFunction("int", valueExpression);

                        case JTokenType.String:
                            // convert string literal to function call via string() function
                            return CreateUnaryFunction("string", valueExpression);
                    }

                    break;
            }

            throw new NotImplementedException($"Unexpected expression type '{converted.GetType().Name}'.");
        }

        private static LanguageExpression ConvertFunction(string functionName, LanguageExpression[] arguments)
        {
            if (string.Equals("any", functionName, LanguageConstants.IdentifierComparison))
            {
                // this is the any function - don't generate a function call for it
                return arguments.Single();
            }

            return new FunctionExpression(functionName, arguments, Array.Empty<LanguageExpression>());
        }

        private FunctionExpression ConvertComplexLiteral(SyntaxBase syntax)
        {
            // the tree node here should not contain any expressions inside
            // if it does, the emitted json expressions will not evaluate as expected due to IL limitations
            // there is a check elsewhere that will generate an error before we get this far in those cases
            var buffer = new StringBuilder();
            using (var writer = new JsonTextWriter(new StringWriter(buffer)) {Formatting = Formatting.None})
            {
                new ExpressionEmitter(writer, this.model).EmitExpression(syntax);
            }

            return CreateJsonFunctionCall(JToken.Parse(buffer.ToString()));
        }

        private LanguageExpression ConvertBinary(BinaryOperationSyntax syntax)
        {
            LanguageExpression operand1 = ConvertExpression(syntax.LeftExpression);
            LanguageExpression operand2 = ConvertExpression(syntax.RightExpression);

            switch (syntax.Operator)
            {
                case BinaryOperator.LogicalOr:
                    return CreateBinaryFunction("or", operand1, operand2);

                case BinaryOperator.LogicalAnd:
                    return CreateBinaryFunction("and", operand1, operand2);

                case BinaryOperator.Equals:
                    return CreateBinaryFunction("equals", operand1, operand2);

                case BinaryOperator.NotEquals:
                    return CreateUnaryFunction("not", 
                        CreateBinaryFunction("equals", operand1, operand2));

                case BinaryOperator.EqualsInsensitive:
                    return CreateBinaryFunction("equals",
                        CreateUnaryFunction("toLower", operand1),
                        CreateUnaryFunction("toLower", operand2));

                case BinaryOperator.NotEqualsInsensitive:
                    return CreateUnaryFunction("not",
                        CreateBinaryFunction("equals",
                            CreateUnaryFunction("toLower", operand1),
                            CreateUnaryFunction("toLower", operand2)));

                case BinaryOperator.LessThan:
                    return CreateBinaryFunction("less", operand1, operand2);

                case BinaryOperator.LessThanOrEqual:
                    return CreateBinaryFunction("lessOrEquals", operand1, operand2);

                case BinaryOperator.GreaterThan:
                    return CreateBinaryFunction("greater", operand1, operand2);

                case BinaryOperator.GreaterThanOrEqual:
                    return CreateBinaryFunction("greaterOrEquals", operand1, operand2);

                case BinaryOperator.Add:
                    return CreateBinaryFunction("add", operand1, operand2);

                case BinaryOperator.Subtract:
                    return CreateBinaryFunction("sub", operand1, operand2);

                case BinaryOperator.Multiply:
                    return CreateBinaryFunction("mul", operand1, operand2);

                case BinaryOperator.Divide:
                    return CreateBinaryFunction("div", operand1, operand2);

                case BinaryOperator.Modulo:
                    return CreateBinaryFunction("mod", operand1, operand2);

                default:
                    throw new NotImplementedException($"Cannot emit unexpected binary operator '{syntax.Operator}'.");
            }
        }

        private LanguageExpression ConvertUnary(UnaryOperationSyntax syntax)
        {
            LanguageExpression convertedOperand = ConvertExpression(syntax.Expression);

            switch (syntax.Operator)
            {
                case UnaryOperator.Not:
                    return CreateUnaryFunction("not", convertedOperand);

                case UnaryOperator.Minus:
                    if (convertedOperand is JTokenExpression literal && literal.Value.Type == JTokenType.Integer)
                    {
                        // invert the integer literal
                        int literalValue = literal.Value.Value<int>();
                        return new JTokenExpression(-literalValue);
                    }

                    return new FunctionExpression(
                        "sub",
                        new[] {new JTokenExpression(0), convertedOperand},
                        Array.Empty<LanguageExpression>());

                default:
                    throw new NotImplementedException($"Cannot emit unexpected unary operator '{syntax.Operator}.");
            }
        }

        private static FunctionExpression AppendProperty(FunctionExpression function, LanguageExpression newProperty) => 
            // technically we could just mutate the provided function object, but let's hold off on that optimization
            // until we have evidence that it is needed
            new FunctionExpression(function.Function, function.Parameters, function.Properties.Append(newProperty).ToArray());

        private static FunctionExpression CreateUnaryFunction(string function, LanguageExpression operand) => 
            new FunctionExpression(function, new[] {operand}, Array.Empty<LanguageExpression>());

        private static FunctionExpression CreateBinaryFunction(string name, LanguageExpression operand1, LanguageExpression operand2) =>
            new FunctionExpression(name, new[] {operand1, operand2}, Array.Empty<LanguageExpression>());

        private static FunctionExpression CreateJsonFunctionCall(JToken value) =>
            CreateUnaryFunction("json", new JTokenExpression(value.ToString(Formatting.None)));
    }
}