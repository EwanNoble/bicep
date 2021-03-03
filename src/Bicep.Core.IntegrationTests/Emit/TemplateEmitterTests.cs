// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Bicep.Core.Emit;
using Bicep.Core.FileSystem;
using Bicep.Core.Parsing;
using Bicep.Core.Samples;
using Bicep.Core.Semantics;
using Bicep.Core.Syntax;
using Bicep.Core.UnitTests;
using Bicep.Core.UnitTests.Assertions;
using Bicep.Core.UnitTests.Utils;
using Bicep.Core.Workspaces;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bicep.Core.IntegrationTests.Emit
{
    [TestClass]
    public class TemplateEmitterTests
    {
        [NotNull]
        public TestContext? TestContext { get; set; }

        [DataTestMethod]
        [DynamicData(nameof(GetValidDataSets), DynamicDataSourceType.Method, DynamicDataDisplayNameDeclaringType = typeof(DataSet), DynamicDataDisplayName = nameof(DataSet.GetDisplayName))]
        public void ValidBicep_TemplateEmiterShouldProduceExpectedTemplate(DataSet dataSet)
        {
            var outputDirectory = dataSet.SaveFilesToTestDirectory(TestContext, dataSet.Name);
            var bicepFilePath = Path.Combine(outputDirectory, DataSet.TestFileMain);
            var compiledFilePath = FileHelper.GetResultFilePath(this.TestContext, Path.Combine(dataSet.Name, DataSet.TestFileMainCompiled));

            // emitting the template should be successful
            var result = this.EmitTemplate(SyntaxTreeGroupingBuilder.Build(new FileResolver(), new Workspace(), PathHelper.FilePathToFileUrl(bicepFilePath)), compiledFilePath, BicepTestConstants.DevAssemblyFileVersion);
            result.Status.Should().Be(EmitStatus.Succeeded);
            // TODO: remove Where when the the support of parameter modifiers is dropped.
            result.Diagnostics.Where(d => d.Code != "BCP153").Should().BeEmpty();

            var actual = JToken.Parse(File.ReadAllText(compiledFilePath));

            actual.Should().EqualWithJsonDiffOutput(
                TestContext, 
                JToken.Parse(dataSet.Compiled!),
                expectedLocation: DataSet.GetBaselineUpdatePath(dataSet, DataSet.TestFileMainCompiled),
                actualLocation: compiledFilePath);
        }

        [TestMethod]
        public void TemplateEmitter_output_should_not_include_UTF8_BOM()
        {
            var syntaxTreeGrouping = SyntaxTreeGroupingFactory.CreateFromText("");
            var compiledFilePath = FileHelper.GetResultFilePath(this.TestContext, "main.json");

            // emitting the template should be successful
            var result = this.EmitTemplate(syntaxTreeGrouping, compiledFilePath, BicepTestConstants.DevAssemblyFileVersion);
            result.Status.Should().Be(EmitStatus.Succeeded);
            result.Diagnostics.Should().BeEmpty();

            var bytes = File.ReadAllBytes(compiledFilePath);
            // No BOM at the start of the file
            bytes.Take(3).Should().NotBeEquivalentTo(new [] { 0xEF, 0xBB, 0xBF }, "BOM should not be present");
            bytes.First().Should().Be(0x7B, "template should always begin with a UTF-8 encoded open curly");
            bytes.Last().Should().Be(0x7D, "template should always end with a UTF-8 encoded close curly");
        }

        [DataTestMethod]
        [DynamicData(nameof(GetValidDataSets), DynamicDataSourceType.Method, DynamicDataDisplayNameDeclaringType = typeof(DataSet), DynamicDataDisplayName = nameof(DataSet.GetDisplayName))]
        public void ValidBicepTextWriter_TemplateEmiterShouldProduceExpectedTemplate(DataSet dataSet)
        {
            var outputDirectory = dataSet.SaveFilesToTestDirectory(TestContext, dataSet.Name);
            var bicepFilePath = Path.Combine(outputDirectory, DataSet.TestFileMain);
            MemoryStream memoryStream = new MemoryStream();

            // emitting the template should be successful
            var result = this.EmitTemplate(SyntaxTreeGroupingBuilder.Build(new FileResolver(), new Workspace(), PathHelper.FilePathToFileUrl(bicepFilePath)), memoryStream, BicepTestConstants.DevAssemblyFileVersion);
            // TODO: remove Where when the the support of parameter modifiers is dropped.
            result.Diagnostics.Where(d => d.Code != "BCP153").Should().BeEmpty();
            result.Status.Should().Be(EmitStatus.Succeeded);

            // normalizing the formatting in case there are differences in indentation
            // this way the diff between actual and expected will be clean
            var actual = JToken.ReadFrom(new JsonTextReader(new StreamReader(new MemoryStream(memoryStream.ToArray()))));
            var compiledFilePath = FileHelper.SaveResultFile(this.TestContext, Path.Combine(dataSet.Name, DataSet.TestFileMainCompiled), actual.ToString(Formatting.Indented));

            actual.Should().EqualWithJsonDiffOutput(
                TestContext, 
                JToken.Parse(dataSet.Compiled!),
                expectedLocation: DataSet.GetBaselineUpdatePath(dataSet, DataSet.TestFileMainCompiled),
                actualLocation: compiledFilePath);
        }

        [DataTestMethod]
        [DynamicData(nameof(GetValidDataSets), DynamicDataSourceType.Method, DynamicDataDisplayNameDeclaringType = typeof(DataSet), DynamicDataDisplayName = nameof(DataSet.GetDisplayName))]
        public void ValidBicepTextWriter_TemplateEmiterTemplateHashCheck(DataSet dataSet)
        {
            var outputDirectory = dataSet.SaveFilesToTestDirectory(TestContext, dataSet.Name);
            var bicepFilePath = Path.Combine(outputDirectory, DataSet.TestFileMain);
            MemoryStream memoryStream = new MemoryStream();

            // emitting the template should be successful
            var result = this.EmitTemplate(SyntaxTreeGroupingBuilder.Build(new FileResolver(), new Workspace(), PathHelper.FilePathToFileUrl(bicepFilePath)), memoryStream, ThisAssembly.AssemblyFileVersion);
            // TODO: remove Where when the the support of parameter modifiers is dropped.
            result.Diagnostics.Where(d => d.Code != "BCP153").Should().BeEmpty();
            result.Status.Should().Be(EmitStatus.Succeeded);

            var actual = JToken.ReadFrom(new JsonTextReader(new StreamReader(new MemoryStream(memoryStream.ToArray()))));
            var compiled = JToken.Parse(dataSet.Compiled!);
            
            // TemplateHash should not be the same with difference assembly versions
            actual.SelectToken("metadata._generator.templateHash")!.ToString().Should().NotBe(
                compiled.SelectToken("metadata._generator.templateHash")!.ToString()
            );
            actual.SelectToken("metadata._generator.version")!.ToString().Should().Be(ThisAssembly.AssemblyFileVersion);
            
            // Aside from the different metadata, the templates should be the same
            ((JObject) actual).Remove("metadata");
            ((JObject) compiled).Remove("metadata");

            var compiledFilePath = FileHelper.SaveResultFile(this.TestContext, Path.Combine(dataSet.Name, DataSet.TestFileMainCompiled), actual.ToString(Formatting.Indented));
            actual.Should().EqualWithJsonDiffOutput(
                TestContext, 
                compiled,
                expectedLocation: DataSet.GetBaselineUpdatePath(dataSet, DataSet.TestFileMainCompiled),
                actualLocation: compiledFilePath);
        }

        [DataTestMethod]
        [DynamicData(nameof(GetInvalidDataSets), DynamicDataSourceType.Method, DynamicDataDisplayNameDeclaringType = typeof(DataSet), DynamicDataDisplayName = nameof(DataSet.GetDisplayName))]
        public void InvalidBicep_TemplateEmiterShouldNotProduceAnyTemplate(DataSet dataSet)
        {
            var outputDirectory = dataSet.SaveFilesToTestDirectory(TestContext, dataSet.Name);
            var bicepFilePath = Path.Combine(outputDirectory, DataSet.TestFileMain);
            string filePath = FileHelper.GetResultFilePath(this.TestContext, $"{dataSet.Name}_Compiled_Original.json");

            // emitting the template should fail
            var result = this.EmitTemplate(SyntaxTreeGroupingBuilder.Build(new FileResolver(), new Workspace(), PathHelper.FilePathToFileUrl(bicepFilePath)), filePath, BicepTestConstants.DevAssemblyFileVersion);
            result.Status.Should().Be(EmitStatus.Failed);
            result.Diagnostics.Should().NotBeEmpty();
        }

        [DataTestMethod]
        [DataRow("\n")]
        [DataRow("\r\n")]
        public void Multiline_strings_should_parse_correctly(string newlineSequence)
        {
            var inputFile = @"
var multiline = '''
this
  is
    a
      multiline
        string
'''
";

            var (template, _, _) = CompilationHelper.Compile(StringUtils.ReplaceNewlines(inputFile, newlineSequence));

            var expected = string.Join(newlineSequence, new [] { "this", "  is", "    a", "      multiline", "        string", "" });
            template!.SelectToken("$.variables.multiline")!.Should().DeepEqual(expected);
        }

        private EmitResult EmitTemplate(SyntaxTreeGrouping syntaxTreeGrouping, string filePath, string assemblyFileVersion)
        {
            var compilation = new Compilation(TestResourceTypeProvider.Create(), syntaxTreeGrouping);
            var emitter = new TemplateEmitter(compilation.GetEntrypointSemanticModel(), assemblyFileVersion);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            return emitter.Emit(stream);
        }

        private EmitResult EmitTemplate(SyntaxTreeGrouping syntaxTreeGrouping, MemoryStream memoryStream, string assemblyFileVersion)
        {
            var compilation = new Compilation(TestResourceTypeProvider.Create(), syntaxTreeGrouping);
            var emitter = new TemplateEmitter(compilation.GetEntrypointSemanticModel(), assemblyFileVersion);

            TextWriter tw = new StreamWriter(memoryStream);
            return emitter.Emit(tw);
        }

        private static IEnumerable<object[]> GetValidDataSets() => DataSets
            .AllDataSets
            .Where(ds => ds.IsValid)
            .ToDynamicTestData();

        private static IEnumerable<object[]> GetInvalidDataSets() => DataSets
            .AllDataSets
            .Where(ds => ds.IsValid == false)
            .ToDynamicTestData();
    }
}

