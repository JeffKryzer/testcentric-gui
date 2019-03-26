// ***********************************************************************
// Copyright (c) 2018 Charlie Poole
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Engine;

namespace TestCentric.Gui.Model
{
    public class TestModel : ITestModel
    {
        private const string PROJECT_LOADER_EXTENSION_PATH = "/NUnit/Engine/TypeExtensions/IProjectLoader";
        private const string NUNIT_PROJECT_LOADER = "NUnit.Engine.Services.ProjectLoaders.NUnitProjectLoader";
        private const string VISUAL_STUDIO_PROJECT_LOADER = "NUnit.Engine.Services.ProjectLoaders.VisualStudioProjectLoader";

        // Our event dispatcher. Events are exposed through the Events
        // property. This is used when firing events from the model.
        private TestEventDispatcher _events;

        // Check if the loaded Assemblies has been changed
        private AssemblyWatcher _assemblyWatcher;

        private bool _lastRunWasDebugRun;

        #region Constructor

        public TestModel(ITestEngine testEngine)
        {
            TestEngine = testEngine;
            Services = new TestServices(testEngine);

            foreach (var node in Services.ExtensionService.GetExtensionNodes(PROJECT_LOADER_EXTENSION_PATH))
            {
                if (node.TypeName == NUNIT_PROJECT_LOADER)
                    NUnitProjectSupport = true;
                else if (node.TypeName == VISUAL_STUDIO_PROJECT_LOADER)
                    VisualStudioSupport = true;
            }

            PackageSettings[EnginePackageSettings.RunAsX86] = Services.UserSettings.Gui.RunAsX86;

            _events = new TestEventDispatcher(this);
            _assemblyWatcher = new AssemblyWatcher();
        }

        #endregion

        #region ITestModel Implementation

        #region General Properties

        // Event Dispatcher
        public ITestEvents Events { get { return _events; } }

        // Services provided either by the model itself or by the engine
        public ITestServices Services { get; }

        // Project Support
        public bool NUnitProjectSupport { get; }
        public bool VisualStudioSupport { get; }

        // Runtime Support
        private List<IRuntimeFramework> _runtimes;
        public IList<IRuntimeFramework> AvailableRuntimes
        {
            get
            {
                if (_runtimes == null)
                    _runtimes = GetAvailableRuntimes();

                return _runtimes;
            }
        }

        // Result Format Support
        private List<string> _resultFormats;
        public IEnumerable<string> ResultFormats
        {
            get
            {
                if (_resultFormats == null)
                {
                    _resultFormats = new List<string>();
                    foreach (string format in Services.ResultService.Formats)
                        _resultFormats.Add(format);
                }

                return _resultFormats;
            }
        }

        #endregion

        #region Current State of the Model

        public bool IsPackageLoaded { get { return TestPackage != null; } }

        public List<string> TestFiles { get; } = new List<string>();

        public TestSelection TestAssemblies { get { return Tests.Select((tn) => tn.Type == "Assembly"); } }

        public IDictionary<string, object> PackageSettings { get; } = new Dictionary<string, object>();

        public TestNode Tests { get; private set; }
        public bool HasTests { get { return Tests != null; } }

        public IList<string> AvailableCategories { get; private set; }

        public bool IsTestRunning
        {
            get { return Runner != null && Runner.IsTestRunning; }
        }

        public bool HasResults
        {
            get { return Results.Count > 0; }
        }

        public List<string> SelectedCategories { get; private set; }

        public bool ExcludeSelectedCategories { get; private set; }

        public TestFilter CategoryFilter { get; private set; } = TestFilter.Empty;

        #endregion

        #region Methods

        public void NewProject()
        {
            NewProject("Dummy");
        }

        public const string PROJECT_SAVE_MESSAGE =
            "It's not yet decided if we will implement saving of projects. The alternative is to require use of the project editor, which already supports this.";

        public void NewProject(string filename)
        {
            throw new NotImplementedException(PROJECT_SAVE_MESSAGE);
        }

        public void SaveProject()
        {
            throw new NotImplementedException(PROJECT_SAVE_MESSAGE);
        }

        public void LoadTests(IList<string> files)
        {
            if (IsPackageLoaded)
                UnloadTests();

            TestFiles.Clear();
            TestFiles.AddRange(files);
            _events.FireTestsLoading(files);

            TestPackage = MakeTestPackage(files);
            _lastRunWasDebugRun = false;

            Runner = TestEngine.GetRunner(TestPackage);
            Tests = ExploreTestPackage(TestPackage);
            AvailableCategories = GetAvailableCategories();

            Results.Clear();

            _assemblyWatcher.Setup(1000, files as IList);
            _assemblyWatcher.AssemblyChanged += new AssemblyChangedHandler(OnChange);
            _assemblyWatcher.Start();
            _events.FireTestLoaded(Tests);

            foreach (var subPackage in TestPackage.SubPackages)
                Services.RecentFiles.SetMostRecent(subPackage.FullName);
        }

        private void OnChange(string fullpath)
        {
            _events.FireTestChanged();
        }

        public void UnloadTests()
        {
            _events.FireTestsUnloading();

            Runner.Unload();
            Tests = null;
            AvailableCategories = null;
            TestPackage = null;
            TestFiles.Clear();
            Results.Clear();
            _assemblyWatcher.Stop();

            _events.FireTestUnloaded();
        }

        public void ReloadTests()
        {
            _events.FireTestsReloading();

            Runner.Reload();

            _lastRunWasDebugRun = false;
            TestPackage = MakeTestPackage(TestFiles);

            //Tests = ExploreTestPackage(TestPackage);
            AvailableCategories = GetAvailableCategories();

            if (Services.UserSettings.Gui.ClearResultsOnReload)
                Results.Clear();

            _events.FireTestReloaded(Tests);
        }

        public void RunAllTests()
        {
            RunTests(CategoryFilter);
        }

        public void RunTests(ITestItem testItem)
        {
            if (testItem == null)
                throw new ArgumentNullException("testItem");

            var filter = testItem.GetTestFilter();

            if (!CategoryFilter.IsEmpty())
                filter = Filters.MakeAndFilter(filter, CategoryFilter);

            RunTests(filter);
        }

        private void RunTests(TestFilter filter)
        {
            if (Services.UserSettings.Gui.ReloadOnRun)
                ReloadTests();

            SetTestDebuggingFlag(false);

            Runner.RunAsync(_events, filter);
        }

        public void DebugAllTests()
        {
            DebugTests(TestFilter.Empty);
        }

        public void DebugTests(ITestItem testItem)
        {
            if (testItem != null) DebugTests(testItem.GetTestFilter());
        }

        private void DebugTests(TestFilter filter)
        {
            SetTestDebuggingFlag(true);

            Runner.RunAsync(_events, filter);
        }

        private void SetTestDebuggingFlag(bool debuggingRequested)
        {
            // We need to re-create the test runner because settings such
            // as debugging have already been passed to the test runner.
            // For performance, only do this if we did run in a different mode than last time.
            if (_lastRunWasDebugRun != debuggingRequested)
            {
                foreach (var subPackage in TestPackage.SubPackages)
                {
                    subPackage.Settings["DebugTests"] = debuggingRequested;
                }

                Runner = TestEngine.GetRunner(TestPackage);
                Runner.Load();

                // It is not strictly necessary to load the tests
                // because the runner will do that automatically, however,
                // the initial test count will be incorrect causing UI crashes.

                _lastRunWasDebugRun = debuggingRequested;
            }
        }

        public void CancelTestRun()
        {
            Runner.StopRun(false);
        }

        public void SaveResults(string filePath, string format = "nunit3")
        {
            var resultWriter = Services.ResultService.GetResultWriter(format, new object[0]);
            var results = GetResultForTest(Tests.Id);
            resultWriter.WriteResultFile(results.Xml, filePath);
        }

        public ResultNode GetResultForTest(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                ResultNode result;
                if (Results.TryGetValue(id, out result))
                    return result;
            }

            return null;
        }

        public void ClearResults()
        {
            Results.Clear();
        }

        public void SelectCategories(IList<string> categories, bool exclude)
        {
            SelectedCategories = new List<string>(categories);
            ExcludeSelectedCategories = exclude;

            UpdateCategoryFilter();

            _events.FireCategorySelectionChanged();
        }

        private void UpdateCategoryFilter()
        {
            var catFilter = TestFilter.Empty;

            if (SelectedCategories != null && SelectedCategories.Count > 0)
            {
                catFilter = Filters.MakeCategoryFilter(SelectedCategories);

                if (ExcludeSelectedCategories)
                    catFilter = Filters.MakeNotFilter(catFilter);
            }

            CategoryFilter = catFilter;
        }

        public void NotifySelectedItemChanged(ITestItem testItem)
        {
            _events.FireSelectedItemChanged(testItem);
        }

        #endregion

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            try
            {
                if (IsPackageLoaded)
                    UnloadTests();

                if (Runner != null)
                    Runner.Dispose();

                if (TestEngine != null)
                    TestEngine.Dispose();

                if (_assemblyWatcher != null)
                    _assemblyWatcher.Dispose();
            }
            catch (NUnitEngineUnloadException)
            {
                // TODO: Figure out what to do about this
            }
        }

        #endregion

        #region Private Properties

        private ITestEngine TestEngine { get; }

        private ITestRunner Runner { get; set; }

        private TestPackage TestPackage { get; set; }

        internal IDictionary<string, ResultNode> Results { get; } = new Dictionary<string, ResultNode>();

        #endregion

        #region Helper Methods

        // Public for testing only
        public TestPackage MakeTestPackage(IList<string> testFiles)
        {
            var package = new TestPackage(testFiles);
            var engineSettings = Services.UserSettings.Engine;

            // We use AddSetting rather than just setting the value because
            // it propagates the setting to all subprojects.

            if (engineSettings.ProcessModel != "Default")
                package.AddSetting(EnginePackageSettings.ProcessModel, engineSettings.ProcessModel);

            // Don't set DomainUsage for Multiple to avoid engine error
            if (engineSettings.ProcessModel != "Multiple")
                package.AddSetting(EnginePackageSettings.DomainUsage, engineSettings.DomainUsage);
            // Don't bother checking Agents unless we are running multiple processes
            else if (engineSettings.Agents > 0)
                package.AddSetting(EnginePackageSettings.MaxAgents, engineSettings.Agents);

            if (engineSettings.SetPrincipalPolicy)
                package.AddSetting(EnginePackageSettings.PrincipalPolicy, engineSettings.PrincipalPolicy);

            //if (Options.InternalTraceLevel != null)
            //    package.AddSetting(EnginePackageSettings.InternalTraceLevel, Options.InternalTraceLevel);

            package.AddSetting(EnginePackageSettings.ShadowCopyFiles, engineSettings.ShadowCopyFiles);

            foreach (var subpackage in package.SubPackages)
                if (Path.GetExtension(subpackage.Name) == ".sln")
                    subpackage.AddSetting(EnginePackageSettings.SkipNonTestAssemblies, true);

            foreach (var entry in PackageSettings)
                package.AddSetting(entry.Key, entry.Value);

            return package;
        }

        // TODO: The NUnit engine is not returning project elements for each project
        // so we create them in this method. Remove when the engine is fixed.
        private TestNode ExploreTestPackage(TestPackage package)
        {
            var tests = new TestNode(Runner.Explore(TestFilter.Empty));
            int nextId = 10;
            int nextTest = 0;
            int nextPackage = 0;

            while (nextPackage < package.SubPackages.Count && nextTest < tests.Children.Count)
            {
                var subPackage = package.SubPackages[nextPackage++];
                var childTest = tests.Children[nextTest];

                if (subPackage.Name != childTest.Name)
                {
                    // SubPackage did not match the next child test node, so it must be a project.
                    var project = new TestNode($"<test-suite type='Project' id='{nextId++}' name='{subPackage.Name}' fullname='{subPackage.FullName}' runstate='Runnable'/>");
                    int testCount = 0;

                    // Now move any children of this project under it
                    foreach (var subSubPackage in subPackage.SubPackages)
                    {
                        childTest = tests.Children[nextTest];

                        if (subSubPackage.Name == childTest.Name)
                        {
                            project.Children.Add(childTest);
                            tests.Children.RemoveAt(nextTest);
                            testCount += childTest.TestCount;
                        }
                    }

                    project.Xml.AddAttribute("testcasecount", testCount.ToString());
                    tests.Children.Insert(nextTest, project);
                }

                nextTest++;
            }

            return tests;
        }

        // The engine returns more values than we really want.
        // For example, we don't currently distinguish between
        // client and full profiles when executing tests. We
        // drop unwanted entries here. Even if some of these items
        // are removed in a later version of the engine, we may
        // have to retain this code to work with older engines.
        private List<IRuntimeFramework> GetAvailableRuntimes()
        {
            var runtimes = new List<IRuntimeFramework>();

            foreach (var runtime in Services.GetService<IAvailableRuntimes>().AvailableRuntimes)
            {
                // Nothing below 2.0
                if (runtime.ClrVersion.Major < 2)
                    continue;

                // Remove erroneous entries for 4.5 Client profile
                if (IsErroneousNet45ClientProfile(runtime))
                    continue;

                // Skip duplicates
                var duplicate = false;
                foreach (var rt in runtimes)
                    if (rt.DisplayName == runtime.DisplayName)
                    {
                        duplicate = true;
                        break;
                    }

                if (!duplicate)
                    runtimes.Add(runtime);
            }

            runtimes.Sort((x, y) => x.DisplayName.CompareTo(y.DisplayName));

            // Now eliminate client entries where full entry follows
            for (int i = runtimes.Count - 2; i >= 0; i--)
            {
                var rt1 = runtimes[i];
                var rt2 = runtimes[i + 1];

                if (rt1.Id != rt2.Id)
                    continue;

                if (rt1.Profile == "Client" && rt2.Profile == "Full")
                    runtimes.RemoveAt(i);
            }

            return runtimes;
        }

        // Some early versions of the engine return .NET 4.5 Client profile, which doesn't really exist
        private static bool IsErroneousNet45ClientProfile(IRuntimeFramework runtime)
        {
            return runtime.FrameworkVersion.Major == 4 && runtime.FrameworkVersion.Minor > 0 && runtime.Profile == "Client";
        }

        public IList<string> GetAvailableCategories()
        {
            var categories = new Dictionary<string, string>();
            CollectCategories(Tests, categories);

            var list = new List<string>(categories.Values);
            list.Sort();
            return list;
        }

        private void CollectCategories(TestNode test, Dictionary<string, string> categories)

        {
            foreach (string name in test.GetPropertyList("Category").Split(new[] { ',' }))
                if (IsValidCategoryName(name))
                    categories[name] = name;

            if (test.IsSuite)
                foreach (TestNode child in test.Children)
                    CollectCategories(child, categories);
        }

        public static bool IsValidCategoryName(string name)
        {
            return name.Length > 0 && name.IndexOfAny(new char[] { ',', '!', '+', '-' }) < 0;
        }

        #endregion
    }
}
