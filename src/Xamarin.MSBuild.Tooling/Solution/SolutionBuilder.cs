// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.Build.Evaluation;

using static Xamarin.GuidHelpers;
using static Xamarin.PathHelpers;

namespace Xamarin.MSBuild.Tooling.Solution
{
    public sealed class SolutionBuilder
    {
        // Used as the namespace for v5 project GUIDs  based off their
        // relative path when an explicit project GUID is not provided
        static readonly Guid solutionGuid = new Guid ("{17ad6350-380a-4d65-9b2c-aa44b5da8111}");

        readonly SolutionNode solution = new SolutionNode ();

        readonly List<ConfigurationPlatform> solutionConfigurations = new List<ConfigurationPlatform> ();
        public IReadOnlyList<ConfigurationPlatform> SolutionConfigurations => solutionConfigurations;

        public string FileName { get; }

        public SolutionBuilder (string fileName)
        {
            FileName = fileName ?? throw new ArgumentNullException (nameof (fileName));
        }

        public void Write (string fileName = null)
            => SolutionWriter.Write (
                solution,
                SolutionConfigurations,
                fileName ?? FileName);

        public void Write (TextWriter writer)
            => SolutionWriter.Write (
                solution,
                SolutionConfigurations,
                writer);

        public void AddSolutionConfiguration (ConfigurationPlatform solutionConfiguration)
        {
            if (!solutionConfigurations.Contains (solutionConfiguration))
                solutionConfigurations.Add (solutionConfiguration);
        }

        public void AddProject (
            string projectPath,
            IEnumerable<SolutionConfigurationPlatformMap> configurations,
            string solutionFolder = null,
            Guid projectGuid = default)
        {
            var node = AddProject (projectPath, solutionFolder, projectGuid);
            foreach (var configuration in configurations ?? Array.Empty<SolutionConfigurationPlatformMap> ())
                node.AddConfigurationMap (configuration);
        }

        SolutionNode AddProject (
            string projectPath,
            string solutionFolder = null,
            Guid projectGuid = default)
        {
            if (projectPath == null)
                throw new ArgumentNullException (nameof (projectPath));

            var solutionDirectory = Path.GetDirectoryName (FileName);
            if (string.IsNullOrEmpty (solutionDirectory))
                solutionDirectory = ".";

            var relativePath = MakeRelativePath (solutionDirectory, projectPath);

            if (projectGuid == default)
                // for the GUID, force path separators so Win vs Mac produces the same
                projectGuid = GuidV5 (solutionGuid, relativePath.Replace ('\\', '/'));

            var parentNode = solution;

            if (!string.IsNullOrEmpty (solutionFolder)) {
                foreach (var name in solutionFolder.Split ('\\', '/'))
                    parentNode = parentNode.AddFolder (name);
            }

            return parentNode.AddProject (
                projectGuid,
                relativePath);
        }

        /// <summary>
        /// Creates a new <see cref="SolutionBuilder"/> from an MSBuild traversal project.
        /// </summary>
        /// <param name="projectPath">
        /// The path to the traversal project from which to populate the returned <see cref="SolutionBuilder"/>.
        /// </param>
        /// <param name="solutionOutputPath">
        /// The path to the solution that the returned <see cref="SolutionBuilder"/> should
        /// represent. This path is used to compute the relative path to projects.
        /// </param>
        /// <param name="addAllProjectReferences">
        /// Whether or not to add transient `&lt;ProjectReference&gt;` projects to the solution.
        /// </param>
        public static SolutionBuilder FromTraversalProject (
            string projectPath,
            string solutionOutputPath = null,
            bool addTransientProjectReferences = true)
        {
            if (projectPath == null)
                throw new ArgumentNullException (nameof (projectPath));

            if (!File.Exists (projectPath))
                throw new FileNotFoundException ($"project does not exist: {projectPath}");

            projectPath = ResolveFullPath (projectPath);

            if (solutionOutputPath == null)
                solutionOutputPath = Path.ChangeExtension (projectPath, ".sln");

            var solution = new SolutionBuilder (solutionOutputPath);
            var traversalProject = new ProjectCollection ().LoadProject (projectPath);

            // Add the explicit solution configurations
            foreach (var item in traversalProject.GetItems ("SolutionConfiguration"))
                solution.AddSolutionConfiguration (ConfigurationPlatform.Parse (item.EvaluatedInclude));

            // For each solution configuration, re-evaluate the whole project collection
            // with the mapped solution -> project configurations. This can result in
            // different project references (e.g. cross platform projects that might build
            // on windows and not mac, etc.).
            foreach (var item in traversalProject.GetItems ("SolutionConfiguration")) {
                var solutionConfigurationPlatform = ConfigurationPlatform.Parse (item.EvaluatedInclude);
                var projectConfigurationPlatform = new ConfigurationPlatform (
                    item.GetMetadataValue ("Configuration") ?? solutionConfigurationPlatform.Configuration,
                    item.GetMetadataValue ("Platform") ?? solutionConfigurationPlatform.Platform);

                var globalProperties = new List<(string, string)> {
                    ("Configuration", projectConfigurationPlatform.Configuration),
                    ("Platform", projectConfigurationPlatform.Platform)
                };
                globalProperties.AddRange (item.Metadata.Select (m => (m.Name, m.EvaluatedValue)));

                var graph = DependencyGraph
                    .Create (projectPath, globalProperties)
                    .LoadGraph ();

                // Add each project. Projects may fail to load in MSBuild, for example, if an SDK
                // is not available via the running MSBuild toolchain. If this is the case, we
                // still need to handle adding the project to the solution, we just can't infer
                // anything therein.
                foreach (var node in graph.TopologicallySortedProjects) {
                    // Don't add the root traversal project to the solution
                    if (node.Parents.Count == 0)
                        continue;

                    // Prefer an explicit project GUID if one exists (old style projects)
                    Guid projectGuid = default;
                    if (node.Project != null) {
                        var explicitProjectGuid = node.Project.GetPropertyValue ("ProjectGuid");
                        if (!string.IsNullOrEmpty (explicitProjectGuid))
                            Guid.TryParse (explicitProjectGuid, out projectGuid);
                    }

                    var solutionNode = solution.AddProject (
                        node.ProjectPath,
                        null,
                        projectGuid);

                    solutionNode.AddConfigurationMap (new SolutionConfigurationPlatformMap (
                        solutionConfigurationPlatform,
                        projectConfigurationPlatform
                    ));
                }
            }

            return solution;
        }
    }
}