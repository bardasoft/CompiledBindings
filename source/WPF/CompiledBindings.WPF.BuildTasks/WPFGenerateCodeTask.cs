﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil.Rocks;

namespace CompiledBindings;

public class WPFGenerateCodeTask : Task, ICancelableTask
{
	private CancellationTokenSource? _cancellationTokenSource;

	[Required]
	public required string LangVersion { get; init; }

	[Required]
	public required string MSBuildVersion { get; init; }

	[Required]
	public required ITaskItem[] ReferenceAssemblies { get; init; }

	[Required]
	public required string LocalAssembly { get; init; }

	[Required]
	public required string ProjectPath { get; init; }

	[Required]
	public required string IntermediateOutputPath { get; init; }

	public required ITaskItem ApplicationDefinition { get; init; }

	[Required]
	public required ITaskItem[] Pages { get; init; }

	[Output]
	public ITaskItem NewApplicationDefinition { get; private set; } = null!;

	[Output]
	public ITaskItem[] NewPages { get; private set; } = null!;

	[Output]
	public ITaskItem[] GeneratedCodeFiles { get; private set; } = null!;

	public bool AttachDebugger { get; set; }

	public override bool Execute()
	{
		try
		{
			if (AttachDebugger)
			{
				System.Diagnostics.Debugger.Launch();
			}

			_cancellationTokenSource = new CancellationTokenSource();

			TypeInfoUtils.LoadReferences(ReferenceAssemblies.Select(a => a.ItemSpec));

			var helperTypeAssembly = TypeInfoUtils.Assemblies
				.FirstOrDefault(a => a.MainModule.GetAllTypes().Any(t => t.FullName == "CompiledBindings.WPF.CompiledBindingsHelper"))?
				.Name.Name;

			var localAssembly = TypeInfoUtils.LoadLocalAssembly(LocalAssembly);

			var xamlDomParser = new WpfXamlDomParser();

			var generatedCodeFiles = new List<ITaskItem>();
			var newPages = new List<ITaskItem>();
			bool result = true;

			var allXaml = Pages.ToList();
			if (ApplicationDefinition != null)
			{
				allXaml.Add(ApplicationDefinition);
			}

			var xamlFiles = allXaml
				.Distinct(f => f.GetMetadata("FullPath"))
				.Select(f => (xaml: f, file: f.GetMetadata("FullPath")))
				.Select(e => (e.xaml, e.file, xdoc: XDocument.Load(e.file, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo)))
				.ToList();

			var globalNamespaces = XamlNamespace.GetGlobalNamespaces(xamlFiles.Select(e => e.xdoc));
			xamlDomParser.KnownNamespaces = globalNamespaces;

			var intermediateOutputPath = IntermediateOutputPath;
			bool isIntermediateOutputPathRooted = Path.IsPathRooted(intermediateOutputPath);
			if (!isIntermediateOutputPathRooted)
			{
				intermediateOutputPath = Path.Combine(ProjectPath, intermediateOutputPath);
			}

			foreach (var (xaml, file, xdoc) in xamlFiles)
			{
				var newXaml = xaml;

				var targetRelativePath = xaml.GetMetadata("Link");
				if (string.IsNullOrEmpty(targetRelativePath))
				{
					targetRelativePath = xaml.ItemSpec;
				}

				var targetDir = Path.Combine(IntermediateOutputPath, Path.GetDirectoryName(targetRelativePath));
				var sourceCodeTargetPath = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(targetRelativePath) + ".g.m.cs");
				var xamlFile = Path.Combine(IntermediateOutputPath, targetRelativePath);

				try
				{
					var xclass = xdoc.Root.Attribute(xamlDomParser.xClass);
					if (xclass != null)
					{
						string lineFile;
						if (!isIntermediateOutputPathRooted)
						{
							var realPath = Path.Combine(ProjectPath, Path.GetDirectoryName(targetRelativePath));
							var intermediatePath = Path.Combine(intermediateOutputPath, Path.GetDirectoryName(targetRelativePath));
							var relativePath = PathUtils.GetRelativePath(intermediatePath, realPath);
							lineFile = Path.Combine(relativePath, Path.GetFileName(targetRelativePath));
						}
						else
						{
							lineFile = targetRelativePath;
						}

						var parseResult = xamlDomParser.Parse(file, lineFile, xdoc, (line, startColumn, endColumn, message) => Log.LogError(null, null, null, file, line, startColumn, line, endColumn, message));
						if (parseResult == null)
						{
							result = false;
						}
						else if (parseResult.GenerateCode)
						{
							var codeGenerator = new WpfCodeGenerator(LangVersion, MSBuildVersion);
							string code = codeGenerator.GenerateCode(parseResult);

							code = GenerateUtils.GeneratedCodeHeader + Environment.NewLine + code;

							var dirInfo = new DirectoryInfo(targetDir);
							dirInfo.Create();

							File.WriteAllText(sourceCodeTargetPath, code);
							generatedCodeFiles.Add(new TaskItem(sourceCodeTargetPath));

							bool generateDataTemplates = parseResult.DataTemplates.Any(dt => dt.GenerateClass);

							if (generateDataTemplates)
							{
								var compiledBindingsNs = "clr-namespace:CompiledBindings.WPF";
								if (helperTypeAssembly != null)
								{
									compiledBindingsNs += ";assembly=" + helperTypeAssembly;
								}
								var localNs = "clr-namespace:" + parseResult.TargetType!.Reference.Namespace;

								ensureNamespaceDeclared(compiledBindingsNs);
								ensureNamespaceDeclared(localNs);

								void ensureNamespaceDeclared(string searchedClrNs)
								{
									var attr = xdoc.Root.Attributes().FirstOrDefault(a => a.Name.Namespace == XNamespace.Xmlns && a.Value == searchedClrNs);
									if (attr == null)
									{
										string classNsPrefix;
										int nsIndex = 0;
										do
										{
											classNsPrefix = "g" + nsIndex++;
										}
										while (xdoc.Root.Attributes().Any(a =>
											a.Name.Namespace == XNamespace.Xmlns && a.Name.LocalName == classNsPrefix));

										xdoc.Root.Add(new XAttribute(XNamespace.Xmlns + classNsPrefix, searchedClrNs));
									}
								}

								var mbui = (XNamespace)compiledBindingsNs;
								var local = (XNamespace)localNs;

								for (int i = 0; i < parseResult.DataTemplates.Count; i++)
								{
									var dataTemplate = parseResult.DataTemplates[i];
									if (dataTemplate.GenerateClass)
									{
										var rootElement = dataTemplate.RootElement.Elements().First();

										rootElement.Add(
											new XElement(mbui + "CompiledBindingsHelper.Bindings",
												new XElement(local + $"{parseResult.TargetType.Reference.Name}_DataTemplate{i}",
													dataTemplate.EnumerateResources().Select(r => new XAttribute(r.name, $"{{StaticResource {r.name}}}")))));
									}
								}
							}

							foreach (var obj in parseResult.EnumerateAllObjects().Where(o => !o.NameExplicitlySet && o.Name != null))
							{
								((XElement)obj.XamlNode.Element).Add(new XAttribute(xamlDomParser.xNamespace + "Name", obj.Name));
							}

							foreach (var prop in parseResult.EnumerateAllProperties())
							{
								var prop2 = prop.XamlNode.Element.Parent.Attribute(prop.XamlNode.Name);
								prop2?.Remove();
							}

							xdoc.Save(xamlFile);

							newXaml = new TaskItem(xamlFile);
							xaml.CopyMetadataTo(newXaml);
							newXaml.SetMetadata("Link", targetRelativePath);
						}
					}
				}
				catch (Exception ex) when (ex is not GeneratorException)
				{
					throw new GeneratorException(ex.Message, file, 0, 0, 0);
				}

				setNewXaml();

				void setNewXaml()
				{
					if (xaml == ApplicationDefinition)
					{
						NewApplicationDefinition = newXaml;
					}
					else
					{
						newPages.Add(newXaml);
					}
				}
			}

			if (generatedCodeFiles.Count > 0 && helperTypeAssembly == null)
			{
				var dataTemplateBindingsFile = Path.Combine(IntermediateOutputPath, "CompiledBindingsHelper.WPF.cs");
				File.WriteAllText(dataTemplateBindingsFile, GenerateCompiledBindingsHelper());
				generatedCodeFiles.Add(new TaskItem(dataTemplateBindingsFile));
			}

			GeneratedCodeFiles = generatedCodeFiles.ToArray();
			NewPages = newPages.ToArray();

			foreach (var lrefFile in Directory.GetFiles(IntermediateOutputPath, "*.lref"))
			{
				File.Delete(lrefFile);
			}
			foreach (var lrefFile in Directory.GetFiles(IntermediateOutputPath, "*.cache"))
			{
				File.Delete(lrefFile);
			}

			return result;
		}
		catch (GeneratorException ex)
		{
			Log.LogError(null, null, null, ex.File, ex.LineNumber, ex.ColumnNumber, ex.EndLineNumber, ex.EndColumnNumber, ex.Message);
			return false;
		}
		catch (Exception ex)
		{
			Log.LogError(ex.Message);
			return false;
		}
		finally
		{
			TypeInfoUtils.Cleanup();
		}
	}

	private static string GenerateCompiledBindingsHelper()
	{
		return
@"namespace CompiledBindings.WPF
{
	public class CompiledBindingsHelper
	{
		public static void SetPropertyChangedEventHandler(ref global::System.ComponentModel.INotifyPropertyChanged? cache, global::System.ComponentModel.INotifyPropertyChanged? source, global::System.ComponentModel.PropertyChangedEventHandler handler)
		{
			if (cache != null && !object.ReferenceEquals(cache, source))
			{
				cache.PropertyChanged -= handler;
				cache = null;
			}
			if (cache == null && source != null)
			{
				cache = source;
				cache.PropertyChanged += handler;
			}
		}

		public static void SetPropertyChangedEventHandler(ref global::System.ComponentModel.INotifyPropertyChanged? cache, object? source, global::System.ComponentModel.PropertyChangedEventHandler handler)
		{
			if (cache != null && !object.ReferenceEquals(cache, source))
			{
				cache.PropertyChanged -= handler;
				cache = null;
			}
			if (cache == null && source is global::System.ComponentModel.INotifyPropertyChanged npc)
			{
				cache = npc;
				cache.PropertyChanged += handler;
			}
		}

		public static T? TryGetBindings<T>(ref global::System.WeakReference? bindingsWeakReference, global::System.Action cleanup)
			where T : class
		{
			T? bindings = null;
			if (bindingsWeakReference != null)
			{
				bindings = (T?)bindingsWeakReference.Target;
				if (bindings == null)
				{
					bindingsWeakReference = null;
					cleanup();
				}
			}
			return bindings;
		}

		public static readonly global::System.Windows.DependencyProperty BindingsProperty =
			global::System.Windows.DependencyProperty.RegisterAttached(""Bindings"", typeof(IGeneratedDataTemplate), typeof(CompiledBindingsHelper), new global::System.Windows.PropertyMetadata(BindingsChanged));

		public static IGeneratedDataTemplate GetBindings(global::System.Windows.DependencyObject @object)
		{
			return (IGeneratedDataTemplate)@object.GetValue(BindingsProperty);
		}

		public static void SetBindings(global::System.Windows.DependencyObject @object, IGeneratedDataTemplate value)
		{
			@object.SetValue(BindingsProperty, value);
		}

		static void BindingsChanged(global::System.Windows.DependencyObject d, global::System.Windows.DependencyPropertyChangedEventArgs e)
		{
			if (e.OldValue != null)
			{
				((IGeneratedDataTemplate)e.OldValue).Cleanup((global::System.Windows.FrameworkElement)d);
			}
			if (e.NewValue != null)
			{
				((IGeneratedDataTemplate)e.NewValue).Initialize((global::System.Windows.FrameworkElement)d);
			}
		}
	}

	public interface IGeneratedDataTemplate
	{
		void Initialize(global::System.Windows.FrameworkElement rootElement);
		void Cleanup(global::System.Windows.FrameworkElement rootElement);
	}
}";
	}

	void ICancelableTask.Cancel()
	{
		_cancellationTokenSource?.Cancel();
	}
}

public class WpfXamlDomParser : SimpleXamlDomParser
{
	private readonly static XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
	private ILookup<string, string>? _nsMappings = null;

	public WpfXamlDomParser()
		: base("http://schemas.microsoft.com/winfx/2006/xaml/presentation",
			   xNs,
			   TypeInfo.GetTypeThrow("System.Windows.Data.IValueConverter"),
			   TypeInfo.GetTypeThrow("System.Windows.Data.BindingBase"),
			   TypeInfo.GetTypeThrow("System.Windows.DependencyObject"),
			   TypeInfo.GetTypeThrow("System.Windows.DependencyProperty"))
	{
	}

	protected override IEnumerable<string> GetClrNsFromXmlNs(string xmlNs)
	{
		_nsMappings ??= TypeInfoUtils.Assemblies
							.SelectMany(ass => ass.CustomAttributes.Where(at => at.AttributeType.FullName == "System.Windows.Markup.XmlnsDefinitionAttribute"))
							.Select(at => (
								XmlNamespace: (string)at.ConstructorArguments[0].Value,
								ClrNamespace: (string)at.ConstructorArguments[1].Value))
							.ToLookup(at => at.XmlNamespace, at => at.ClrNamespace);

		return _nsMappings[xmlNs];
	}

	protected override ExtenstionType? IsMemExtension(XName name)
	{
		var res = base.IsMemExtension(name);
		if (res == null)
		{
			if (name.Namespace == xNs)
			{
				res = name.LocalName switch
				{
					"Bind" or "BindExtension" => ExtenstionType.Bind,
					"Set" or "SetExtension" => ExtenstionType.Set,
					_ => null
				};
			}
		}
		return res;
	}
}

public class WpfCodeGenerator : SimpleXamlDomCodeGenerator
{
	public WpfCodeGenerator(string langVersion, string msbuildVersion)
		: base(new WpfBindingsCodeGenerator(langVersion, msbuildVersion),
			   "Data",
			   "System.Windows.DependencyPropertyChangedEventArgs",
			   "System.Windows.FrameworkElement",
			   "(global::{0}){1}.FindName(\"{2}\")",
			   false,
			   false,
			   "WPF",
			   langVersion,
			   msbuildVersion)
	{
	}

	protected override string CreateGetResourceCode(string resourceName, int varIndex)
	{
		return $@"this.Resources[""{resourceName}""] ?? global::System.Windows.Application.Current.Resources[""{resourceName}""] ?? throw new global::System.Exception(""Resource '{resourceName}' not found."")";
	}
}

public class WpfBindingsCodeGenerator : BindingsCodeGenerator
{
	public WpfBindingsCodeGenerator(string langVersion, string msbuildVersion)
		: base("WPF", langVersion, msbuildVersion)
	{
	}

	protected override void GenerateSetDependencyPropertyChangedCallback(StringBuilder output, TwoWayBindingData ev, string targetExpr)
	{
		var dp = ev.Bindings[0].DependencyProperty!;
		output.AppendLine(
$@"				global::System.ComponentModel.DependencyPropertyDescriptor
					.FromProperty(
						global::{dp.Definition.DeclaringType.GetCSharpFullName()}.{dp.Definition.Name},
						typeof(global::{dp.Definition.DeclaringType.GetCSharpFullName()}))
					.AddValueChanged({targetExpr}, OnTargetChanged{ev.Index});");
	}

	protected override void GenerateUnsetDependencyPropertyChangedCallback(StringBuilder output, TwoWayBindingData ev, string targetExpr)
	{
		var dp = ev.Bindings[0].DependencyProperty!;
		output.AppendLine(
$@"					global::System.ComponentModel.DependencyPropertyDescriptor
						.FromProperty(
							global::{dp.Definition.DeclaringType.GetCSharpFullName()}.{dp.Definition.Name},
							typeof(global::{dp.Definition.DeclaringType.GetCSharpFullName()}))
						.RemoveValueChanged({targetExpr}, OnTargetChanged{ev.Index});");
	}

	protected override void GenerateDependencyPropertyChangedCallback(StringBuilder output, string methodName, string? a)
	{
		output.AppendLine(
$@"{a}			private void {methodName}(object sender, global::System.EventArgs e)");
	}

	protected override void GenerateDependencyPropertyChangeCacheVariables(StringBuilder output, NotifySource notifySource)
	{
		output.AppendLine(
$@"				global::System.Windows.DependencyObject _propertyChangeSource{notifySource.Index};");
	}

	protected override void GenerateRegisterDependencyPropertyChangeEvent(StringBuilder output, NotifySource notifySource, NotifyProperty notifyProp, string cacheVar, string methodName)
	{
		output.AppendLine(
$@"						global::System.ComponentModel.DependencyPropertyDescriptor
							.FromProperty(
								global::{notifySource.Expression.Type.Reference.GetCSharpFullName()}.{notifyProp.Member!.Definition.Name}Property, typeof(global::{notifySource.Expression.Type.Reference.GetCSharpFullName()}))
							.AddValueChanged({cacheVar}, {methodName});");
	}

	protected override void GenerateUnregisterDependencyPropertyChangeEvent(StringBuilder output, NotifySource notifySource, NotifyProperty notifyProp, string cacheVar, string methodName)
	{
		output.AppendLine(
$@"						global::System.ComponentModel.DependencyPropertyDescriptor
							.FromProperty(
								global::{notifySource.Expression.Type.Reference.GetCSharpFullName()}.{notifyProp.Member!.Definition.Name}Property, typeof(global::{notifySource.Expression.Type.Reference.GetCSharpFullName()}))
							.RemoveValueChanged({cacheVar}, {methodName});");
	}
}

