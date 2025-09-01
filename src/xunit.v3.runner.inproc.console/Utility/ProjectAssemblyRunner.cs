using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit.Internal;
using Xunit.Runner.Common;
using Xunit.Sdk;
using Xunit.v3;
using DiagnosticMessage = Xunit.Runner.Common.DiagnosticMessage;
using ErrorMessage = Xunit.Runner.Common.ErrorMessage;

namespace Xunit.Runner.InProc.SystemConsole;

/// <summary>
/// The project assembly runner class, used by <see cref="ConsoleRunner"/>.
/// </summary>
/// <param name="testAssembly">The assembly under test</param>
/// <param name="automatedMode">The automated mode we're running in</param>
/// <param name="sourceInformationProvider">The source information provider</param>
/// <param name="cancellationTokenSource">The cancellation token source used to indicate cancellation</param>
public sealed class ProjectAssemblyRunner(
	Assembly testAssembly,
	AutomatedMode automatedMode,
	ISourceInformationProvider sourceInformationProvider,
	CancellationTokenSource cancellationTokenSource)
{
	readonly AutomatedMode automatedMode = automatedMode;
	readonly CancellationTokenSource cancellationTokenSource = Guard.ArgumentNotNull(cancellationTokenSource);
	bool failed;
	readonly Assembly testAssembly = testAssembly;

	/// <summary>
	/// Initializes an instance of the <see cref="ProjectAssemblyRunner"/> class, without support
	/// for source information.
	/// </summary>
	/// <param name="testAssembly">The assembly under test</param>
	/// <param name="automatedMode">The automated mode we're running in</param>
	/// <param name="cancellationTokenSource">The cancellation token source used to indicate cancellation</param>
	public ProjectAssemblyRunner(
		Assembly testAssembly,
		AutomatedMode automatedMode,
		CancellationTokenSource cancellationTokenSource) :
			this(testAssembly, automatedMode, NullSourceInformationProvider.Instance, cancellationTokenSource)
	{ }

	/// <summary>
	/// Gets a one-line banner to be printed when the runner is executed.
	/// </summary>
	public static string Banner =>
		string.Format(
			CultureInfo.CurrentCulture,
			"xUnit.net v3 In-Process Runner v{0} ({1}-bit {2})",
			ThisAssembly.AssemblyInformationalVersion,
			IntPtr.Size * 8,
			RuntimeInformation.FrameworkDescription
		);

	/// <summary>
	/// Gets the summaries of the test execution, once it is finished.
	/// </summary>
	public TestExecutionSummaries TestExecutionSummaries { get; } = new();

	/// <summary>
	/// Discovers tests in the given test project.
	/// </summary>
	/// <param name="assembly">The test project assembly</param>
	/// <param name="pipelineStartup">The pipeline startup object</param>
	/// <param name="messageSink">The optional message sink to send messages to</param>
	/// <param name="diagnosticMessageSink">The optional message sink to send diagnostic messages to</param>
	/// <param name="testCases">A collection to contain the test cases to run, if desired</param>
	public async ValueTask Discover(
		XunitProjectAssembly assembly,
		ITestPipelineStartup? pipelineStartup,
		IMessageSink? messageSink = null,
		IMessageSink? diagnosticMessageSink = null,
		IList<(ITestCase TestCase, bool PassedFilter)>? testCases = null)
	{
		Guard.ArgumentNotNull(assembly);

		// Setup discovery options with command-line overrides
		var discoveryOptions = TestFrameworkOptions.ForDiscovery(assembly.Configuration);
		var diagnosticMessages = assembly.Configuration.DiagnosticMessagesOrDefault;
		var internalDiagnosticMessages = assembly.Configuration.InternalDiagnosticMessagesOrDefault;

		TestContext.SetForInitialization(diagnosticMessageSink, diagnosticMessages, internalDiagnosticMessages);

		await using var disposalTracker = new DisposalTracker();
		var testFramework = ExtensibilityPointFactory.GetTestFramework(testAssembly);
		disposalTracker.Add(testFramework);

		if (pipelineStartup is not null)
			testFramework.SetTestPipelineStartup(pipelineStartup);

		var frontController = new InProcessFrontController(testFramework, testAssembly, assembly.ConfigFileName);

		await frontController.Find(
			messageSink,
			discoveryOptions,
			testCase => assembly.Configuration.Filters.Filter(Path.GetFileNameWithoutExtension(assembly.AssemblyFileName), testCase),
			cancellationTokenSource,
			discoveryCallback: (testCase, passedFilter) =>
			{
				testCases?.Add((testCase, passedFilter));

				return
					passedFilter && (messageSink?.OnMessage(testCase.ToTestCaseDiscovered().WithSourceInfo(sourceInformationProvider))) == false
						? new(false)
						: new(!cancellationTokenSource.IsCancellationRequested);
			}
		);

		TestContextInternal.Current.SafeDispose();
	}

	/// <summary>
	/// Invoke the instance of <see cref="ITestPipelineStartup"/>, if it exists, and returns the instance
	/// that was created.
	/// </summary>
	/// <param name="testAssembly">The test assembly under test</param>
	/// <param name="diagnosticMessageSink">The optional diagnostic message sink to report diagnostic messages to</param>
	public static async ValueTask<ITestPipelineStartup?> InvokePipelineStartup(
		Assembly testAssembly,
		IMessageSink? diagnosticMessageSink)
	{
		Guard.ArgumentNotNull(testAssembly);

		var warnings = new List<string>();

		try
		{
			var result = default(ITestPipelineStartup);

			var pipelineStartupAttributes = testAssembly.GetMatchingCustomAttributes<ITestPipelineStartupAttribute>(warnings);
			if (pipelineStartupAttributes.Count > 1)
				throw new TestPipelineException("More than one pipeline startup attribute was specified: " + pipelineStartupAttributes.Select(a => a.GetType()).ToCommaSeparatedList());

			if (pipelineStartupAttributes.FirstOrDefault() is ITestPipelineStartupAttribute pipelineStartupAttribute)
			{
				var pipelineStartupType = pipelineStartupAttribute.TestPipelineStartupType;
				if (!typeof(ITestPipelineStartup).IsAssignableFrom(pipelineStartupType))
					throw new TestPipelineException(string.Format(CultureInfo.CurrentCulture, "Pipeline startup type '{0}' does not implement '{1}'", pipelineStartupType.SafeName(), typeof(ITestPipelineStartup).SafeName()));

				try
				{
					result = Activator.CreateInstance(pipelineStartupType) as ITestPipelineStartup;
				}
				catch (Exception ex)
				{
					throw new TestPipelineException(string.Format(CultureInfo.CurrentCulture, "Pipeline startup type '{0}' threw during construction", pipelineStartupType.SafeName()), ex);
				}

				if (result is null)
					throw new TestPipelineException(string.Format(CultureInfo.CurrentCulture, "Pipeline startup type '{0}' does not implement '{1}'", pipelineStartupType.SafeName(), typeof(ITestPipelineStartup).SafeName()));

				await result.StartAsync(diagnosticMessageSink ?? NullMessageSink.Instance);
			}

			return result;
		}
		finally
		{
			if (diagnosticMessageSink is not null)
				foreach (var warning in warnings)
					diagnosticMessageSink.OnMessage(new DiagnosticMessage(warning));
		}
	}

	/// <summary>
	/// Prints the program header.
	/// </summary>
	/// <param name="consoleHelper">The console helper to use for output</param>
	public static void PrintHeader(ConsoleHelper consoleHelper) =>
		Guard.ArgumentNotNull(consoleHelper).WriteLine(
			"xUnit.net v3 In-Process Runner v{0} ({1}-bit {2})",
			ThisAssembly.AssemblyInformationalVersion,
			IntPtr.Size * 8,
			RuntimeInformation.FrameworkDescription
		);

	/// <summary>
	/// Runs the given test project.
	/// </summary>
	/// <param name="assembly">The test project assembly</param>
	/// <param name="messageSink">The message sink to send messages to</param>
	/// <param name="diagnosticMessageSink">The optional message sink to send diagnostic messages to</param>
	/// <param name="runnerLogger">The runner logger, to log console output to</param>
	/// <param name="pipelineStartup">The pipeline startup object</param>
	/// <param name="testCaseIDsToRun">An optional list of test case unique IDs to run</param>
	/// <returns>Returns <c>0</c> if there were no failures; non-<c>zero</c> failure count, otherwise</returns>
	public async ValueTask<int> Run(
		XunitProjectAssembly assembly,
		IMessageSink messageSink,
		IMessageSink? diagnosticMessageSink,
		IRunnerLogger runnerLogger,
		ITestPipelineStartup? pipelineStartup,
		HashSet<string>? testCaseIDsToRun = null)
	{
		Guard.ArgumentNotNull(assembly);
		Guard.ArgumentNotNull(messageSink);
		Guard.ArgumentNotNull(runnerLogger);

		XElement? assemblyElement = null;
		var clockTime = Stopwatch.StartNew();
		var xmlTransformers = TransformFactory.GetXmlTransformers(assembly.Project);
		var needsXml = xmlTransformers.Count > 0;

		if (needsXml)
			assemblyElement = new XElement("assembly");

		var originalWorkingFolder = Directory.GetCurrentDirectory();

		try
		{
			// Setup discovery and execution options with command-line overrides
			var discoveryOptions = TestFrameworkOptions.ForDiscovery(assembly.Configuration);
			var executionOptions = TestFrameworkOptions.ForExecution(assembly.Configuration);

			var diagnosticMessages = assembly.Configuration.DiagnosticMessagesOrDefault;
			var internalDiagnosticMessages = assembly.Configuration.InternalDiagnosticMessagesOrDefault;
			var longRunningSeconds = assembly.Configuration.LongRunningTestSecondsOrDefault;

			TestContext.SetForInitialization(diagnosticMessageSink, diagnosticMessages, internalDiagnosticMessages);

			try
			{
				await using var disposalTracker = new DisposalTracker();
				var testFramework = ExtensibilityPointFactory.GetTestFramework(testAssembly);
				disposalTracker.Add(testFramework);

				if (pipelineStartup is not null)
					testFramework.SetTestPipelineStartup(pipelineStartup);

				var frontController = new InProcessFrontController(testFramework, testAssembly, assembly.ConfigFileName);

				var sinkOptions = new ExecutionSinkOptions
				{
					AssemblyElement = assemblyElement,
					CancelThunk = () => cancellationTokenSource.IsCancellationRequested,
					DiagnosticMessageSink = diagnosticMessageSink,
					FailSkips = assembly.Configuration.FailSkipsOrDefault,
					FailWarn = assembly.Configuration.FailTestsWithWarningsOrDefault,
					LongRunningTestTime = TimeSpan.FromSeconds(longRunningSeconds),
				};

				using var resultsSink = new ExecutionSink(assembly, discoveryOptions, executionOptions, AppDomainOption.NotAvailable, shadowCopy: false, messageSink, sinkOptions, sourceInformationProvider);
				var testCasesToRun = new List<ITestCase>();

				foreach (var testCaseToRun in assembly.TestCasesToRun)
					try
					{
						if (SerializationHelper.Instance.Deserialize(testCaseToRun) is ITestCase testCase)
							testCasesToRun.Add(testCase);
					}
					catch { }

				testCaseIDsToRun ??= [];
				testCaseIDsToRun.AddRange(assembly.TestCaseIDsToRun);

				if (testCasesToRun.Count == 0 && testCaseIDsToRun.Count == 0)
					await frontController.FindAndRun(
						resultsSink,
						discoveryOptions,
						executionOptions,
						testCase => assembly.Configuration.Filters.Filter(Path.GetFileNameWithoutExtension(assembly.AssemblyFileName), testCase),
						cancellationTokenSource
					);
				else
				{
					// Convert test case IDs into test cases via discovery
					if (testCaseIDsToRun.Count != 0)
						await frontController.Find(
							resultsSink,
							discoveryOptions,
							testCase => assembly.Configuration.Filters.Filter(Path.GetFileNameWithoutExtension(assembly.AssemblyFileName), testCase),
							cancellationTokenSource,
							discoveryCallback: (testCase, passedFilter) =>
							{
								if (passedFilter && testCaseIDsToRun.Contains(testCase.UniqueID))
									testCasesToRun.Add(testCase);

								return new(true);
							}
						);

					if (assembly.AutoEnableExplicit && testCasesToRun.All(testCase => testCase.Explicit))
						executionOptions.SetExplicitOption(ExplicitOption.Only);

					await frontController.Run(resultsSink, executionOptions, testCasesToRun, cancellationTokenSource);

					foreach (var testCase in testCasesToRun)
						if (testCase is IAsyncDisposable asyncDisposable)
							await asyncDisposable.SafeDisposeAsync();
						else if (testCase is IDisposable disposable)
							disposable.SafeDispose();
				}

				TestExecutionSummaries.Add(frontController.TestAssemblyUniqueID, resultsSink.ExecutionSummary);

				if (resultsSink.ExecutionSummary.Failed != 0 && executionOptions.GetStopOnTestFailOrDefault())
				{
					if (automatedMode != AutomatedMode.Off)
						runnerLogger.WriteMessage(new DiagnosticMessage("Cancelling due to test failure"));
					else
						runnerLogger.LogMessage("Cancelling due to test failure...");

#if NET8_0_OR_GREATER
					await cancellationTokenSource.CancelAsync();
#else
					cancellationTokenSource.Cancel();
#endif
				}
			}
			finally
			{
				TestContextInternal.Current.SafeDispose();
			}
		}
		catch (Exception ex)
		{
			failed = true;

			var e = ex;
			while (e is not null)
			{
				if (automatedMode != AutomatedMode.Off)
					runnerLogger.WriteMessage(ErrorMessage.FromException(e));
				else
				{
					runnerLogger.LogMessage("{0}: {1}", e.GetType().SafeName(), e.Message);

#if DEBUG
					if (e.StackTrace is not null)
#else
					if (assembly.Configuration.InternalDiagnosticMessagesOrDefault && e.StackTrace is not null)
#endif
						runnerLogger.LogMessage(e.StackTrace);
				}

				e = e.InnerException;
			}
		}

		clockTime.Stop();

		TestExecutionSummaries.ElapsedClockTime = clockTime.Elapsed;
		messageSink.OnMessage(TestExecutionSummaries);

		Directory.SetCurrentDirectory(originalWorkingFolder);

		if (assemblyElement is not null)
		{
			var assembliesElement = TransformFactory.CreateAssembliesElement();
			assembliesElement.Add(assemblyElement);
			TransformFactory.FinishAssembliesElement(assembliesElement);

			xmlTransformers.ForEach(transformer => transformer(assembliesElement));
		}

		return failed ? 1 : TestExecutionSummaries.SummariesByAssemblyUniqueID.Sum(s => s.Summary.Failed + s.Summary.Errors);
	}
}
