﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    /// <summary>
    /// A workspace provides access to a active set of source code projects and documents and their
    /// associated syntax trees, compilations and semantic models. A workspace has a current solution
    /// that is an immutable snapshot of the projects and documents. This property may change over time
    /// as the workspace is updated either from live interactions in the environment or via call to the
    /// workspace's <see cref="TryApplyChanges(Solution)"/> method.
    /// </summary>
    public abstract partial class Workspace : IDisposable
    {
        private readonly string? _workspaceKind;
        private readonly HostWorkspaceServices _services;

        private readonly ILegacyWorkspaceOptionService _legacyOptions;

        // forces serialization of mutation calls from host (OnXXX methods). Must take this lock before taking stateLock.
        private readonly SemaphoreSlim _serializationLock = new(initialCount: 1);

        // this lock guards all the mutable fields (do not share lock with derived classes)
        private readonly NonReentrantLock _stateLock = new(useThisInstanceForSynchronization: true);

        // Current solution.
        private Solution _latestSolution;

        private readonly TaskQueue _taskQueue;

        // test hooks.
        internal static bool TestHookStandaloneProjectsDoNotHoldReferences = false;

        internal bool TestHookPartialSolutionsDisabled { get; set; }

        /// <summary>
        /// Determines whether changes made to unchangeable documents will be silently ignored or cause exceptions to be thrown
        /// when they are applied to workspace via <see cref="TryApplyChanges(Solution, IProgressTracker)"/>. 
        /// A document is unchangeable if <see cref="IDocumentOperationService.CanApplyChange"/> is false.
        /// </summary>
        internal virtual bool IgnoreUnchangeableDocumentsWhenApplyingChanges { get; } = false;

        private Action<string>? _testMessageLogger;

        /// <summary>
        /// Constructs a new workspace instance.
        /// </summary>
        /// <param name="host">The <see cref="HostServices"/> this workspace uses</param>
        /// <param name="workspaceKind">A string that can be used to identify the kind of workspace. Usually this matches the name of the class.</param>
        protected Workspace(HostServices host, string? workspaceKind)
        {
            _workspaceKind = workspaceKind;

            _services = host.CreateWorkspaceServices(this);

            _legacyOptions = _services.GetRequiredService<ILegacyWorkspaceOptionService>();
            _legacyOptions.RegisterWorkspace(this);

            // queue used for sending events
            var schedulerProvider = _services.GetRequiredService<ITaskSchedulerProvider>();
            var listenerProvider = _services.GetRequiredService<IWorkspaceAsynchronousOperationListenerProvider>();
            _taskQueue = new TaskQueue(listenerProvider.GetListener(), schedulerProvider.CurrentContextScheduler);

            // initialize with empty solution
            var info = SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create());

            var emptyOptions = new SolutionOptionSet(_legacyOptions);

            _latestSolution = CreateSolution(info, emptyOptions, analyzerReferences: SpecializedCollections.EmptyReadOnlyList<AnalyzerReference>());
        }

        internal void LogTestMessage<TArg>(Func<TArg, string> messageFactory, TArg state)
            => _testMessageLogger?.Invoke(messageFactory(state));

        /// <summary>
        /// Sets an internal logger that will receive some messages.
        /// </summary>
        /// <param name="writeLineMessageLogger">An action called to write a single line to the log.</param>
        internal void SetTestLogger(Action<string>? writeLineMessageLogger)
            => _testMessageLogger = writeLineMessageLogger;

        /// <summary>
        /// Services provider by the host for implementing workspace features.
        /// </summary>
        public HostWorkspaceServices Services => _services;

        /// <summary>
        /// Override this property if the workspace supports partial semantics for documents.
        /// </summary>
        protected internal virtual bool PartialSemanticsEnabled => false;

        /// <summary>
        /// The kind of the workspace.
        /// This is generally <see cref="WorkspaceKind.Host"/> if originating from the host environment, but may be
        /// any other name used for a specific kind of workspace.
        /// </summary>
        // TODO (https://github.com/dotnet/roslyn/issues/37110): decide if Kind should be non-null
        public string? Kind => _workspaceKind;

        /// <summary>
        /// Create a new empty solution instance associated with this workspace.
        /// </summary>
        protected internal Solution CreateSolution(SolutionInfo solutionInfo)
        {
            var options = new SolutionOptionSet(_legacyOptions);
            return CreateSolution(solutionInfo, options, solutionInfo.AnalyzerReferences);
        }

        /// <summary>
        /// Create a new empty solution instance associated with this workspace, and with the given options.
        /// </summary>
        private Solution CreateSolution(SolutionInfo solutionInfo, SolutionOptionSet options, IReadOnlyList<AnalyzerReference> analyzerReferences)
            => new(this, solutionInfo.Attributes, options, analyzerReferences);

        /// <summary>
        /// Create a new empty solution instance associated with this workspace.
        /// </summary>
        protected internal Solution CreateSolution(SolutionId id)
            => CreateSolution(SolutionInfo.Create(id, VersionStamp.Create()));

        /// <summary>
        /// The current solution.
        ///
        /// The solution is an immutable model of the current set of projects and source documents.
        /// It provides access to source text, syntax trees and semantics.
        ///
        /// This property may change as the workspace reacts to changes in the environment or
        /// after <see cref="TryApplyChanges(Solution)"/> is called.
        /// </summary>
        public Solution CurrentSolution
        {
            get
            {
                return Volatile.Read(ref _latestSolution);
            }
        }

        /// <summary>
        /// Sets the <see cref="CurrentSolution"/> of this workspace. This method does not raise a <see cref="WorkspaceChanged"/> event.
        /// </summary>
        /// <remarks>
        /// This method does not guarantee that linked files will have the same contents. Callers
        /// should enforce that policy before passing in the new solution.
        /// </remarks>
        protected Solution SetCurrentSolution(Solution solution)
        {
            if (solution is null)
            {
                throw new ArgumentNullException(nameof(solution));
            }

            var currentSolution = Volatile.Read(ref _latestSolution);
            if (solution == currentSolution)
            {
                // No change
                return solution;
            }

            while (true)
            {
                var newSolution = solution.WithNewWorkspace(this, currentSolution.WorkspaceVersion + 1);
                var oldSolution = Interlocked.CompareExchange(ref _latestSolution, newSolution, currentSolution);
                if (oldSolution == currentSolution)
                {
                    return newSolution;
                }

                currentSolution = oldSolution;
            }
        }

        /// <summary>
        /// Applies specified transformation to <see cref="CurrentSolution"/>, updates <see cref="CurrentSolution"/> to the new value and raises a workspace change event of the specified kind.
        /// </summary>
        /// <param name="transformation">Solution transformation.</param>
        /// <param name="kind">The kind of workspace change event to raise.</param>
        /// <param name="projectId">The id of the project updated by <paramref name="transformation"/> to be passed to the workspace change event.</param>
        /// <param name="documentId">The id of the document updated by <paramref name="transformation"/> to be passed to the workspace change event.</param>
        /// <returns>True if <see cref="CurrentSolution"/> was set to the transformed solution, false if the transformation did not change the solution.</returns>
        internal bool SetCurrentSolution(
            Func<Solution, Solution> transformation,
            WorkspaceChangeKind kind,
            ProjectId? projectId = null,
            DocumentId? documentId = null)
        {
            return SetCurrentSolution(
                static (oldSolution, data) => data.transformation(oldSolution),
                onAfterUpdate: static (oldSolution, newSolution, data) =>
                {
                    // Queue the event but don't execute its handlers on this thread.
                    // Doing so under the serialization lock guarantees the same ordering of the events
                    // as the order of the changes made to the solution.
                    data.@this.RaiseWorkspaceChangedEventAsync(data.kind, oldSolution, newSolution, data.projectId, data.documentId);
                },
                (@this: this, transformation, kind, projectId, documentId));
        }

        /// <summary>
        /// Applies specified transformation to <see cref="CurrentSolution"/>, updates <see cref="CurrentSolution"/> to the new value and raises a workspace change event of the specified kind.
        /// </summary>
        /// <param name="transformation">Solution transformation.</param>
        /// <param name="onAfterUpdate">Action to perform once <see cref="CurrentSolution"/> has been updated.  The
        /// action will be passed the old <see cref="CurrentSolution"/> that was just replaced and the exact solution it
        /// was replaced with. The latter may be different than the solution returned by <paramref
        /// name="transformation"/> as it will have its <see cref="Solution.WorkspaceVersion"/> updated accordingly.
        /// Updating the solution and invoking <paramref name="onAfterUpdate"/> will happen atomically while <see
        /// cref="_serializationLock"/> is being held.</param>
        internal bool SetCurrentSolution<TData>(
            Func<Solution, TData, Solution> transformation,
            Action<Solution, Solution, TData> onAfterUpdate,
            TData data)
        {
            Contract.ThrowIfNull(transformation);

            var currentSolution = Volatile.Read(ref _latestSolution);

            while (true)
            {
                var transformedSolution = transformation(currentSolution, data);
                if (transformedSolution == currentSolution)
                {
                    return false;
                }

                var newSolution = transformedSolution.WithNewWorkspace(this, currentSolution.WorkspaceVersion + 1);

                Solution oldSolution;
                using (_serializationLock.DisposableWait())
                {
                    oldSolution = Interlocked.CompareExchange(ref _latestSolution, newSolution, currentSolution);
                    if (oldSolution == currentSolution)
                    {
                        onAfterUpdate(oldSolution, newSolution, data);
                        return true;
                    }
                }

                currentSolution = oldSolution;
            }
        }

        /// <summary>
        /// Gets or sets the set of all global options and <see cref="Solution.Options"/>.
        /// Setter also force updates the <see cref="CurrentSolution"/> to have the updated <see cref="Solution.Options"/>.
        /// </summary>
        public OptionSet Options
        {
            get
            {
                return this.CurrentSolution.Options;
            }

            [Obsolete(@"Workspace options should be set by invoking 'workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(newOptionSet))'")]
            set
            {
                var changedOptionKeys = value switch
                {
                    null => throw new ArgumentNullException(nameof(value)),
                    SolutionOptionSet serializableOptionSet => serializableOptionSet.GetChangedOptions(),
                    _ => throw new ArgumentException(WorkspacesResources.Options_did_not_come_from_specified_Solution, paramName: nameof(value))
                };

                _legacyOptions.SetOptions(value, changedOptionKeys);
            }
        }

        internal void UpdateCurrentSolutionOnOptionsChanged()
        {
            SetCurrentSolution(CurrentSolution.WithOptions(new SolutionOptionSet(_legacyOptions)));
        }

        /// <summary>
        /// Executes an action as a background task, as part of a sequential queue of tasks.
        /// </summary>
        [SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods", Justification = "This is a Task wrapper, not an asynchronous method.")]
        protected internal Task ScheduleTask(Action action, string? taskName = "Workspace.Task")
            => _taskQueue.ScheduleTask(taskName ?? "Workspace.Task", action, CancellationToken.None);

        /// <summary>
        /// Execute a function as a background task, as part of a sequential queue of tasks.
        /// </summary>
        [SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods", Justification = "This is a Task wrapper, not an asynchronous method.")]
        protected internal Task<T> ScheduleTask<T>(Func<T> func, string? taskName = "Workspace.Task")
            => _taskQueue.ScheduleTask(taskName ?? "Workspace.Task", func, CancellationToken.None);

        /// <summary>
        /// Override this method to act immediately when the text of a document has changed, as opposed
        /// to waiting for the corresponding workspace changed event to fire asynchronously.
        /// </summary>
        protected virtual void OnDocumentTextChanged(Document document)
        {
        }

        /// <summary>
        /// Override this method to act immediately when a document is closing, as opposed
        /// to waiting for the corresponding workspace changed event to fire asynchronously.
        /// </summary>
        protected virtual void OnDocumentClosing(DocumentId documentId)
        {
        }

        /// <summary>
        /// Clears all solution data and empties the current solution.
        /// </summary>
        protected void ClearSolution()
        {
            using (_serializationLock.DisposableWait())
            {
                var oldSolution = this.CurrentSolution;
                this.ClearSolutionData();

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.SolutionCleared, oldSolution, this.CurrentSolution);
            }
        }

        /// <summary>
        /// This method is called when a solution is cleared.
        ///
        /// Override this method if you want to do additional work when a solution is cleared.
        /// Call the base method at the end of your method.
        /// </summary>
        protected virtual void ClearSolutionData()
        {
            // clear any open documents
            this.ClearOpenDocuments();

            this.SetCurrentSolution(this.CreateSolution(this.CurrentSolution.Id));
        }

        /// <summary>
        /// This method is called when an individual project is removed.
        ///
        /// Override this method if you want to do additional work when a project is removed.
        /// Call the base method at the end of your method.
        /// </summary>
        protected virtual void ClearProjectData(ProjectId projectId)
            => this.ClearOpenDocuments(projectId);

        /// <summary>
        /// This method is called to clear an individual document is removed.
        ///
        /// Override this method if you want to do additional work when a document is removed.
        /// Call the base method at the end of your method.
        /// </summary>
        protected internal virtual void ClearDocumentData(DocumentId documentId)
            => this.ClearOpenDocument(documentId);

        /// <summary>
        /// Disposes this workspace. The workspace can longer be used after it is disposed.
        /// </summary>
        public void Dispose()
            => this.Dispose(finalize: false);

        /// <summary>
        /// Call this method when the workspace is disposed.
        ///
        /// Override this method to do additional work when the workspace is disposed.
        /// Call this method at the end of your method.
        /// </summary>
        protected virtual void Dispose(bool finalize)
        {
            if (!finalize)
            {
                this.ClearSolutionData();

                this.Services.GetService<IWorkspaceEventListenerService>()?.Stop();
            }

            _legacyOptions.UnregisterWorkspace(this);

            // Directly dispose IRemoteHostClientProvider if necessary. This is a test hook to ensure RemoteWorkspace
            // gets disposed in unit tests as soon as TestWorkspace gets disposed. This would be superseded by direct
            // support for IDisposable in https://github.com/dotnet/roslyn/pull/47951.
            if (Services.GetService<IRemoteHostClientProvider>() is IDisposable disposableService)
            {
                disposableService.Dispose();
            }
        }

        #region Host API

        /// <summary>
        /// Call this method to respond to a solution being opened in the host environment.
        /// </summary>
        protected internal void OnSolutionAdded(SolutionInfo solutionInfo)
        {
            using (_serializationLock.DisposableWait())
            {
                var oldSolution = this.CurrentSolution;
                var solutionId = solutionInfo.Id;

                CheckSolutionIsEmpty();
                this.SetCurrentSolution(this.CreateSolution(solutionInfo));

                solutionInfo.Projects.Do(p => OnProjectAdded_NoLock(p, silent: true));

                var newSolution = this.CurrentSolution;
                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.SolutionAdded, oldSolution, newSolution);
            }
        }

        /// <summary>
        /// Call this method to respond to a solution being reloaded in the host environment.
        /// </summary>
        protected internal void OnSolutionReloaded(SolutionInfo reloadedSolutionInfo)
        {
            using (_serializationLock.DisposableWait())
            {
                var oldSolution = this.CurrentSolution;
                var newSolution = this.SetCurrentSolution(this.CreateSolution(reloadedSolutionInfo));

                reloadedSolutionInfo.Projects.Do(pi => OnProjectAdded_NoLock(pi, silent: true));

                newSolution = this.AdjustReloadedSolution(oldSolution, this.CurrentSolution);
                newSolution = this.SetCurrentSolution(newSolution);

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.SolutionReloaded, oldSolution, newSolution);
            }
        }

        /// <summary>
        /// This method is called when the solution is removed from the workspace.
        ///
        /// Override this method if you want to do additional work when the solution is removed.
        /// Call the base method at the end of your method.
        /// Call this method to respond to a solution being removed/cleared/closed in the host environment.
        /// </summary>
        protected internal void OnSolutionRemoved()
        {
            using (_serializationLock.DisposableWait())
            {
                var oldSolution = this.CurrentSolution;

                this.ClearSolutionData();

                // reset to new empty solution
                this.SetCurrentSolution(this.CreateSolution(SolutionId.CreateNewId()));

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.SolutionRemoved, oldSolution, this.CurrentSolution);
            }
        }

        /// <summary>
        /// Call this method to respond to a project being added/opened in the host environment.
        /// </summary>
        protected internal void OnProjectAdded(ProjectInfo projectInfo)
            => this.OnProjectAdded(projectInfo, silent: false);

        private void OnProjectAdded(ProjectInfo projectInfo, bool silent)
        {
            using (_serializationLock.DisposableWait())
            {
                this.OnProjectAdded_NoLock(projectInfo, silent);
            }
        }

        private void OnProjectAdded_NoLock(ProjectInfo projectInfo, bool silent)
        {
            var projectId = projectInfo.Id;

            CheckProjectIsNotInCurrentSolution(projectId);

            var oldSolution = this.CurrentSolution;
            var newSolution = this.SetCurrentSolution(oldSolution.AddProject(projectInfo));

            if (!silent)
            {
                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.ProjectAdded, oldSolution, newSolution, projectId);
            }
        }

        /// <summary>
        /// Call this method to respond to a project being reloaded in the host environment.
        /// </summary>
        protected internal virtual void OnProjectReloaded(ProjectInfo reloadedProjectInfo)
        {
            using (_serializationLock.DisposableWait())
            {
                var projectId = reloadedProjectInfo.Id;

                CheckProjectIsInCurrentSolution(projectId);

                var oldSolution = this.CurrentSolution;
                var newSolution = oldSolution.RemoveProject(projectId).AddProject(reloadedProjectInfo);

                newSolution = this.AdjustReloadedProject(oldSolution.GetRequiredProject(projectId), newSolution.GetRequiredProject(projectId)).Solution;
                newSolution = this.SetCurrentSolution(newSolution);

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.ProjectReloaded, oldSolution, newSolution, projectId);
            }
        }

        /// <summary>
        /// Call this method to respond to a project being removed from the host environment.
        /// </summary>
        protected internal virtual void OnProjectRemoved(ProjectId projectId)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckProjectIsInCurrentSolution(projectId);
                this.CheckProjectCanBeRemoved(projectId);

                var oldSolution = this.CurrentSolution;

                this.ClearProjectData(projectId);
                var newSolution = this.SetCurrentSolution(oldSolution.RemoveProject(projectId));

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.ProjectRemoved, oldSolution, newSolution, projectId);
            }
        }

        /// <summary>
        /// Currently projects can always be removed, but this method still exists because it's protected and we don't
        /// want to break people who may have derived from <see cref="Workspace"/> and either called it, or overridden it.
        /// </summary>
        protected virtual void CheckProjectCanBeRemoved(ProjectId projectId)
        {
        }

        /// <summary>
        /// Call this method when a project's assembly name is changed in the host environment.
        /// </summary>
        protected internal void OnAssemblyNameChanged(ProjectId projectId, string assemblyName)
            => SetCurrentSolution(oldSolution => oldSolution.WithProjectAssemblyName(projectId, assemblyName), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a project's output file path is changed in the host environment.
        /// </summary>
        protected internal void OnOutputFilePathChanged(ProjectId projectId, string? outputFilePath)
            => SetCurrentSolution(oldSolution => oldSolution.WithProjectOutputFilePath(projectId, outputFilePath), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a project's output ref file path is changed in the host environment.
        /// </summary>
        protected internal void OnOutputRefFilePathChanged(ProjectId projectId, string? outputFilePath)
            => SetCurrentSolution(oldSolution => oldSolution.WithProjectOutputRefFilePath(projectId, outputFilePath), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a project's name is changed in the host environment.
        /// </summary>
        // TODO (https://github.com/dotnet/roslyn/issues/37124): decide if we want to allow "name" to be nullable.
        // As of this writing you can pass null, but rather than updating the project to null it seems it does nothing.
        // I'm leaving this marked as "non-null" so as not to say we actually support that behavior. The underlying
        // requirement is ProjectInfo.ProjectAttributes holds a non-null name, so you can't get a null into this even if you tried.
        protected internal void OnProjectNameChanged(ProjectId projectId, string name, string? filePath)
            => SetCurrentSolution(oldSolution => oldSolution.WithProjectName(projectId, name).WithProjectFilePath(projectId, filePath), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a project's default namespace is changed in the host environment.
        /// </summary>
        internal void OnDefaultNamespaceChanged(ProjectId projectId, string? defaultNamespace)
            => SetCurrentSolution(oldSolution => oldSolution.WithProjectDefaultNamespace(projectId, defaultNamespace), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a project's compilation options are changed in the host environment.
        /// </summary>
        protected internal void OnCompilationOptionsChanged(ProjectId projectId, CompilationOptions options)
            => SetCurrentSolution(oldSolution => oldSolution.WithProjectCompilationOptions(projectId, options), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a project's parse options are changed in the host environment.
        /// </summary>
        protected internal void OnParseOptionsChanged(ProjectId projectId, ParseOptions options)
            => SetCurrentSolution(oldSolution => oldSolution.WithProjectParseOptions(projectId, options), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a project reference is added to a project in the host environment.
        /// </summary>
        protected internal void OnProjectReferenceAdded(ProjectId projectId, ProjectReference projectReference)
        {
            SetCurrentSolution(oldSolution =>
            {
                CheckProjectIsInCurrentSolution(projectReference.ProjectId);
                CheckProjectDoesNotHaveProjectReference(projectId, projectReference);

                // Can only add this P2P reference if it would not cause a circularity.
                CheckProjectDoesNotHaveTransitiveProjectReference(projectId, projectReference.ProjectId);

                return oldSolution.AddProjectReference(projectId, projectReference);
            }, WorkspaceChangeKind.ProjectChanged, projectId);
        }

        /// <summary>
        /// Call this method when a project reference is removed from a project in the host environment.
        /// </summary>
        protected internal void OnProjectReferenceRemoved(ProjectId projectId, ProjectReference projectReference)
        {
            SetCurrentSolution(oldSolution =>
            {
                CheckProjectIsInCurrentSolution(projectReference.ProjectId);
                CheckProjectHasProjectReference(projectId, projectReference);

                return oldSolution.RemoveProjectReference(projectId, projectReference);
            }, WorkspaceChangeKind.ProjectChanged, projectId);
        }

        /// <summary>
        /// Call this method when a metadata reference is added to a project in the host environment.
        /// </summary>
        protected internal void OnMetadataReferenceAdded(ProjectId projectId, MetadataReference metadataReference)
        {
            SetCurrentSolution(oldSolution =>
            {
                CheckProjectDoesNotHaveMetadataReference(projectId, metadataReference);
                return oldSolution.AddMetadataReference(projectId, metadataReference);
            }, WorkspaceChangeKind.ProjectChanged, projectId);
        }

        /// <summary>
        /// Call this method when a metadata reference is removed from a project in the host environment.
        /// </summary>
        protected internal void OnMetadataReferenceRemoved(ProjectId projectId, MetadataReference metadataReference)
        {
            SetCurrentSolution(oldSolution =>
            {
                CheckProjectHasMetadataReference(projectId, metadataReference);
                return oldSolution.RemoveMetadataReference(projectId, metadataReference);
            }, WorkspaceChangeKind.ProjectChanged, projectId);
        }

        /// <summary>
        /// Call this method when an analyzer reference is added to a project in the host environment.
        /// </summary>
        protected internal void OnAnalyzerReferenceAdded(ProjectId projectId, AnalyzerReference analyzerReference)
        {
            SetCurrentSolution(oldSolution =>
            {
                CheckProjectDoesNotHaveAnalyzerReference(projectId, analyzerReference);
                return oldSolution.AddAnalyzerReference(projectId, analyzerReference);
            }, WorkspaceChangeKind.ProjectChanged, projectId);
        }

        /// <summary>
        /// Call this method when an analyzer reference is removed from a project in the host environment.
        /// </summary>
        protected internal void OnAnalyzerReferenceRemoved(ProjectId projectId, AnalyzerReference analyzerReference)
        {
            SetCurrentSolution(oldSolution =>
            {
                CheckProjectHasAnalyzerReference(projectId, analyzerReference);
                return oldSolution.RemoveAnalyzerReference(projectId, analyzerReference);
            }, WorkspaceChangeKind.ProjectChanged, projectId);
        }

        /// <summary>
        /// Call this method when an analyzer reference is added to a project in the host environment.
        /// </summary>
        internal void OnSolutionAnalyzerReferenceAdded(AnalyzerReference analyzerReference)
        {
            SetCurrentSolution(oldSolution =>
            {
                CheckSolutionDoesNotHaveAnalyzerReference(oldSolution, analyzerReference);
                return oldSolution.AddAnalyzerReference(analyzerReference);
            }, WorkspaceChangeKind.SolutionChanged);
        }

        /// <summary>
        /// Call this method when an analyzer reference is removed from a project in the host environment.
        /// </summary>
        internal void OnSolutionAnalyzerReferenceRemoved(AnalyzerReference analyzerReference)
        {
            SetCurrentSolution(oldSolution =>
            {
                CheckSolutionHasAnalyzerReference(oldSolution, analyzerReference);
                return oldSolution.RemoveAnalyzerReference(analyzerReference);
            }, WorkspaceChangeKind.SolutionChanged);
        }

        /// <summary>
        /// Call this method when status of project has changed to incomplete.
        /// See <see cref="ProjectInfo.HasAllInformation"/> for more information.
        /// </summary>
        // TODO: make it public
        internal void OnHasAllInformationChanged(ProjectId projectId, bool hasAllInformation)
            => SetCurrentSolution(oldSolution => oldSolution.WithHasAllInformation(projectId, hasAllInformation), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a project's RunAnalyzers property is changed in the host environment.
        /// </summary>
        internal void OnRunAnalyzersChanged(ProjectId projectId, bool runAnalyzers)
            => SetCurrentSolution(oldSolution => oldSolution.WithRunAnalyzers(projectId, runAnalyzers), WorkspaceChangeKind.ProjectChanged, projectId);

        /// <summary>
        /// Call this method when a document is added to a project in the host environment.
        /// </summary>
        protected internal void OnDocumentAdded(DocumentInfo documentInfo)
        {
            this.SetCurrentSolution(
                oldSolution => oldSolution.AddDocument(documentInfo),
                WorkspaceChangeKind.DocumentAdded, documentId: documentInfo.Id);
        }

        /// <summary>
        /// Call this method when multiple document are added to one or more projects in the host environment.
        /// </summary>
        protected internal void OnDocumentsAdded(ImmutableArray<DocumentInfo> documentInfos)
        {
            this.SetCurrentSolution(
                static (oldSolution, data) => oldSolution.AddDocuments(data.documentInfos),
                onAfterUpdate: static (oldSolution, newSolution, data) =>
                {
                    // Raise ProjectChanged as the event type here. DocumentAdded is presumed by many callers to have a
                    // DocumentId associated with it, and we don't want to be raising multiple events.

                    foreach (var projectId in data.documentInfos.Select(i => i.Id.ProjectId).Distinct())
                        data.@this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.ProjectChanged, oldSolution, newSolution, projectId);
                }, (@this: this, documentInfos));
        }

        /// <summary>
        /// Call this method when a document is reloaded in the host environment.
        /// </summary>
        protected internal void OnDocumentReloaded(DocumentInfo newDocumentInfo)
        {
            var documentId = newDocumentInfo.Id;
            this.SetCurrentSolution(
                oldSolution => oldSolution.RemoveDocument(documentId).AddDocument(newDocumentInfo),
                WorkspaceChangeKind.DocumentReloaded, documentId: documentId);
        }

        /// <summary>
        /// Call this method when a document is removed from a project in the host environment.
        /// </summary>
        protected internal void OnDocumentRemoved(DocumentId documentId)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckDocumentIsInCurrentSolution(documentId);

                this.CheckDocumentCanBeRemoved(documentId);

                var oldSolution = this.CurrentSolution;

                this.ClearDocumentData(documentId);

                var newSolution = this.SetCurrentSolution(oldSolution.RemoveDocument(documentId));

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.DocumentRemoved, oldSolution, newSolution, documentId: documentId);
            }
        }

        protected virtual void CheckDocumentCanBeRemoved(DocumentId documentId)
        {
        }

        /// <summary>
        /// Call this method when the text of a document is changed on disk.
        /// </summary>
        protected internal void OnDocumentTextLoaderChanged(DocumentId documentId, TextLoader loader)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckDocumentIsInCurrentSolution(documentId);

                var oldSolution = this.CurrentSolution;

                var newSolution = oldSolution.WithDocumentTextLoader(documentId, loader, PreservationMode.PreserveValue);
                newSolution = this.SetCurrentSolution(newSolution);

                var newDocument = newSolution.GetDocument(documentId)!;
                this.OnDocumentTextChanged(newDocument);

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.DocumentChanged, oldSolution, newSolution, documentId: documentId);
            }
        }

        /// <summary>
        /// Call this method when the text of a additional document is changed on disk.
        /// </summary>
        protected internal void OnAdditionalDocumentTextLoaderChanged(DocumentId documentId, TextLoader loader)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckAdditionalDocumentIsInCurrentSolution(documentId);

                var oldSolution = this.CurrentSolution;

                var newSolution = oldSolution.WithAdditionalDocumentTextLoader(documentId, loader, PreservationMode.PreserveValue);
                newSolution = this.SetCurrentSolution(newSolution);

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.AdditionalDocumentChanged, oldSolution, newSolution, documentId: documentId);
            }
        }

        /// <summary>
        /// Call this method when the text of a analyzer config document is changed on disk.
        /// </summary>
        protected internal void OnAnalyzerConfigDocumentTextLoaderChanged(DocumentId documentId, TextLoader loader)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckAnalyzerConfigDocumentIsInCurrentSolution(documentId);

                var oldSolution = this.CurrentSolution;

                var newSolution = oldSolution.WithAnalyzerConfigDocumentTextLoader(documentId, loader, PreservationMode.PreserveValue);
                newSolution = this.SetCurrentSolution(newSolution);

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.AnalyzerConfigDocumentChanged, oldSolution, newSolution, documentId: documentId);
            }
        }

        /// <summary>
        /// Call this method when the document info changes, such as the name, folders or file path.
        /// </summary>
        protected internal void OnDocumentInfoChanged(DocumentId documentId, DocumentInfo newInfo)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckDocumentIsInCurrentSolution(documentId);

                var oldSolution = this.CurrentSolution;

                var newSolution = oldSolution;
                var oldAttributes = oldSolution.GetDocument(documentId)!.State.Attributes;

                if (oldAttributes.Name != newInfo.Name)
                {
                    newSolution = newSolution.WithDocumentName(documentId, newInfo.Name);
                }

                if (oldAttributes.Folders != newInfo.Folders)
                {
                    newSolution = newSolution.WithDocumentFolders(documentId, newInfo.Folders);
                }

                if (oldAttributes.FilePath != newInfo.FilePath)
                {
                    // TODO (https://github.com/dotnet/roslyn/issues/37125): Solution.WithDocumentFilePath will throw if
                    // filePath is null, but it's odd because we *do* support null file paths. The suppression here is to silence it
                    // but should be removed when the bug is fixed.
                    newSolution = newSolution.WithDocumentFilePath(documentId, newInfo.FilePath!);
                }

                if (oldAttributes.SourceCodeKind != newInfo.SourceCodeKind)
                {
                    newSolution = newSolution.WithDocumentSourceCodeKind(documentId, newInfo.SourceCodeKind);
                }

                if (newSolution != oldSolution)
                {
                    SetCurrentSolution(newSolution);

                    this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.DocumentInfoChanged, oldSolution, newSolution, documentId: documentId);
                }
            }
        }

        /// <summary>
        /// Call this method when the text of a document is updated in the host environment.
        /// </summary>
        protected internal void OnDocumentTextChanged(DocumentId documentId, SourceText newText, PreservationMode mode)
        {
            OnAnyDocumentTextChanged(
                documentId,
                newText,
                mode,
                CheckDocumentIsInCurrentSolution,
                (solution, docId) => solution.GetRelatedDocumentIds(docId),
                (solution, docId, text, preservationMode) => solution.WithDocumentText(docId, text, preservationMode),
                WorkspaceChangeKind.DocumentChanged,
                isCodeDocument: true);
        }

        /// <summary>
        /// Call this method when the text of an additional document is updated in the host environment.
        /// </summary>
        protected internal void OnAdditionalDocumentTextChanged(DocumentId documentId, SourceText newText, PreservationMode mode)
        {
            OnAnyDocumentTextChanged(
                documentId,
                newText,
                mode,
                CheckAdditionalDocumentIsInCurrentSolution,
                (solution, docId) => ImmutableArray.Create(docId), // We do not support the concept of linked additional documents
                (solution, docId, text, preservationMode) => solution.WithAdditionalDocumentText(docId, text, preservationMode),
                WorkspaceChangeKind.AdditionalDocumentChanged,
                isCodeDocument: false);
        }

        /// <summary>
        /// Call this method when the text of an analyzer config document is updated in the host environment.
        /// </summary>
        protected internal void OnAnalyzerConfigDocumentTextChanged(DocumentId documentId, SourceText newText, PreservationMode mode)
        {
            OnAnyDocumentTextChanged(
                documentId,
                newText,
                mode,
                CheckAnalyzerConfigDocumentIsInCurrentSolution,
                (solution, docId) => ImmutableArray.Create(docId), // We do not support the concept of linked additional documents
                (solution, docId, text, preservationMode) => solution.WithAnalyzerConfigDocumentText(docId, text, preservationMode),
                WorkspaceChangeKind.AnalyzerConfigDocumentChanged,
                isCodeDocument: false);
        }

        /// <summary>
        /// When a <see cref="Document"/>s text is changed, we need to make sure all of the linked
        /// files also have their content updated in the new solution before applying it to the
        /// workspace to avoid the workspace having solutions with linked files where the contents
        /// do not match.
        /// </summary>
        private void OnAnyDocumentTextChanged(
            DocumentId documentId,
            SourceText newText,
            PreservationMode mode,
            Action<DocumentId> checkIsInCurrentSolution,
            Func<Solution, DocumentId, ImmutableArray<DocumentId>> getRelatedDocuments,
            Func<Solution, DocumentId, SourceText, PreservationMode, Solution> updateSolutionWithText,
            WorkspaceChangeKind changeKind,
            bool isCodeDocument)
        {
            using (_serializationLock.DisposableWait())
            {
                checkIsInCurrentSolution(documentId);

                var originalSolution = CurrentSolution;
                var updatedSolution = CurrentSolution;
                var previousSolution = updatedSolution;

                var linkedDocuments = getRelatedDocuments(updatedSolution, documentId);
                var updatedDocumentIds = new List<DocumentId>();

                foreach (var linkedDocument in linkedDocuments)
                {
                    previousSolution = updatedSolution;
                    updatedSolution = updateSolutionWithText(updatedSolution, linkedDocument, newText, mode);
                    if (previousSolution != updatedSolution)
                    {
                        updatedDocumentIds.Add(linkedDocument);
                    }
                }

                // In the case of linked files, we may have already updated all of the linked
                // documents during an earlier call to this method. We may have no work to do here.
                if (updatedDocumentIds.Count > 0)
                {
                    var newSolution = SetCurrentSolution(updatedSolution);

                    // Prior to the unification of the callers of this method, the
                    // OnAdditionalDocumentTextChanged method did not fire any sort of synchronous
                    // update notification event, so we preserve that behavior here.
                    if (isCodeDocument)
                    {
                        foreach (var updatedDocumentId in updatedDocumentIds)
                        {
                            var newDocument = newSolution.GetDocument(updatedDocumentId);
                            Contract.ThrowIfNull(newDocument);
                            OnDocumentTextChanged(newDocument);
                        }
                    }

                    foreach (var updatedDocumentInfo in updatedDocumentIds)
                    {
                        RaiseWorkspaceChangedEventAsync(
                            changeKind,
                            originalSolution,
                            newSolution,
                            documentId: updatedDocumentInfo);
                    }
                }
            }
        }

        /// <summary>
        /// Call this method when the SourceCodeKind of a document changes in the host environment.
        /// </summary>
        protected internal void OnDocumentSourceCodeKindChanged(DocumentId documentId, SourceCodeKind sourceCodeKind)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckDocumentIsInCurrentSolution(documentId);

                var oldSolution = this.CurrentSolution;
                var newSolution = this.SetCurrentSolution(oldSolution.WithDocumentSourceCodeKind(documentId, sourceCodeKind));

                var newDocument = newSolution.GetDocument(documentId)!;
                this.OnDocumentTextChanged(newDocument);

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.DocumentChanged, oldSolution, newSolution, documentId: documentId);
            }
        }

        /// <summary>
        /// Call this method when an additional document is added to a project in the host environment.
        /// </summary>
        protected internal void OnAdditionalDocumentAdded(DocumentInfo documentInfo)
        {
            using (_serializationLock.DisposableWait())
            {
                var documentId = documentInfo.Id;

                CheckProjectIsInCurrentSolution(documentId.ProjectId);
                CheckAdditionalDocumentIsNotInCurrentSolution(documentId);

                var oldSolution = this.CurrentSolution;
                var newSolution = this.SetCurrentSolution(oldSolution.AddAdditionalDocument(documentInfo));

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.AdditionalDocumentAdded, oldSolution, newSolution, documentId: documentId);
            }
        }

        /// <summary>
        /// Call this method when an additional document is removed from a project in the host environment.
        /// </summary>
        protected internal void OnAdditionalDocumentRemoved(DocumentId documentId)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckAdditionalDocumentIsInCurrentSolution(documentId);

                this.CheckDocumentCanBeRemoved(documentId);

                var oldSolution = this.CurrentSolution;

                this.ClearDocumentData(documentId);

                var newSolution = this.SetCurrentSolution(oldSolution.RemoveAdditionalDocument(documentId));

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.AdditionalDocumentRemoved, oldSolution, newSolution, documentId: documentId);
            }
        }

        /// <summary>
        /// Call this method when an analyzer config document is added to a project in the host environment.
        /// </summary>
        protected internal void OnAnalyzerConfigDocumentAdded(DocumentInfo documentInfo)
        {
            using (_serializationLock.DisposableWait())
            {
                var documentId = documentInfo.Id;

                CheckProjectIsInCurrentSolution(documentId.ProjectId);
                CheckAnalyzerConfigDocumentIsNotInCurrentSolution(documentId);

                var oldSolution = this.CurrentSolution;
                var newSolution = this.SetCurrentSolution(oldSolution.AddAnalyzerConfigDocuments(ImmutableArray.Create(documentInfo)));

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.AnalyzerConfigDocumentAdded, oldSolution, newSolution, documentId: documentId);
            }
        }

        /// <summary>
        /// Call this method when an analyzer config document is removed from a project in the host environment.
        /// </summary>
        protected internal void OnAnalyzerConfigDocumentRemoved(DocumentId documentId)
        {
            using (_serializationLock.DisposableWait())
            {
                CheckAnalyzerConfigDocumentIsInCurrentSolution(documentId);

                this.CheckDocumentCanBeRemoved(documentId);

                var oldSolution = this.CurrentSolution;

                this.ClearDocumentData(documentId);

                var newSolution = this.SetCurrentSolution(oldSolution.RemoveAnalyzerConfigDocument(documentId));

                this.RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.AnalyzerConfigDocumentRemoved, oldSolution, newSolution, documentId: documentId);
            }
        }

        /// <summary>
        /// Updates all projects to properly reference other projects as project references instead of metadata references.
        /// </summary>
        protected void UpdateReferencesAfterAdd()
        {
            SetCurrentSolution(
                oldSolution => UpdateReferencesAfterAdd(oldSolution),
                WorkspaceChangeKind.SolutionChanged);

            [System.Diagnostics.Contracts.Pure]
            static Solution UpdateReferencesAfterAdd(Solution solution)
            {
                // Build map from output assembly path to ProjectId
                // Use explicit loop instead of ToDictionary so we don't throw if multiple projects have same output assembly path.
                var outputAssemblyToProjectIdMap = new Dictionary<string, ProjectId>();
                foreach (var p in solution.Projects)
                {
                    if (!string.IsNullOrEmpty(p.OutputFilePath))
                    {
                        outputAssemblyToProjectIdMap[p.OutputFilePath!] = p.Id;
                    }

                    if (!string.IsNullOrEmpty(p.OutputRefFilePath))
                    {
                        outputAssemblyToProjectIdMap[p.OutputRefFilePath!] = p.Id;
                    }
                }

                // now fix each project if necessary
                foreach (var pid in solution.ProjectIds)
                {
                    var project = solution.GetProject(pid)!;

                    // convert metadata references to project references if the metadata reference matches some project's output assembly.
                    foreach (var meta in project.MetadataReferences)
                    {
                        if (meta is PortableExecutableReference pemeta)
                        {
                            // check both Display and FilePath. FilePath points to the actually bits, but Display should match output path if
                            // the metadata reference is shadow copied.
                            if ((!RoslynString.IsNullOrEmpty(pemeta.Display) && outputAssemblyToProjectIdMap.TryGetValue(pemeta.Display, out var matchingProjectId)) ||
                                (!RoslynString.IsNullOrEmpty(pemeta.FilePath) && outputAssemblyToProjectIdMap.TryGetValue(pemeta.FilePath, out matchingProjectId)))
                            {
                                var newProjRef = new ProjectReference(matchingProjectId, pemeta.Properties.Aliases, pemeta.Properties.EmbedInteropTypes);

                                if (!project.ProjectReferences.Contains(newProjRef))
                                {
                                    project = project.AddProjectReference(newProjRef);
                                }

                                project = project.RemoveMetadataReference(meta);
                            }
                        }
                    }

                    solution = project.Solution;
                }

                return solution;
            }
        }

        #endregion

        #region Apply Changes

        /// <summary>
        /// Determines if the specific kind of change is supported by the <see cref="TryApplyChanges(Solution)"/> method.
        /// </summary>
        public virtual bool CanApplyChange(ApplyChangesKind feature)
            => false;

        /// <summary>
        /// Returns <see langword="true"/> if a reference to referencedProject can be added to
        /// referencingProject.  <see langword="false"/> otherwise.
        /// </summary>
        internal virtual bool CanAddProjectReference(ProjectId referencingProject, ProjectId referencedProject)
            => false;

        /// <summary>
        /// Apply changes made to a solution back to the workspace.
        ///
        /// The specified solution must be one that originated from this workspace. If it is not, or the workspace
        /// has been updated since the solution was obtained from the workspace, then this method returns false. This method
        /// will still throw if the solution contains changes that are not supported according to the <see cref="CanApplyChange(ApplyChangesKind)"/>
        /// method.
        /// </summary>
        /// <exception cref="NotSupportedException">Thrown if the solution contains changes not supported according to the
        /// <see cref="CanApplyChange(ApplyChangesKind)"/> method.</exception>
        public virtual bool TryApplyChanges(Solution newSolution)
            => TryApplyChanges(newSolution, new ProgressTracker());

        internal virtual bool TryApplyChanges(Solution newSolution, IProgressTracker progressTracker)
        {
            using (Logger.LogBlock(FunctionId.Workspace_ApplyChanges, CancellationToken.None))
            {
                // If solution did not originate from this workspace then fail
                if (newSolution.Workspace != this)
                {
                    Logger.Log(FunctionId.Workspace_ApplyChanges, "Apply Failed: workspaces do not match");
                    return false;
                }

                var oldSolution = this.CurrentSolution;

                // If the workspace has already accepted an update, then fail
                if (newSolution.WorkspaceVersion != oldSolution.WorkspaceVersion)
                {
                    Logger.Log(
                        FunctionId.Workspace_ApplyChanges,
                        static (oldSolution, newSolution) =>
                        {
                            // 'oldSolution' is the current workspace solution; if we reach this point we know
                            // 'oldSolution' is newer than the expected workspace solution 'newSolution'.
                            var oldWorkspaceVersion = oldSolution.WorkspaceVersion;
                            var newWorkspaceVersion = newSolution.WorkspaceVersion;
                            return $"Apply Failed: Workspace has already been updated (from version '{newWorkspaceVersion}' to '{oldWorkspaceVersion}')";
                        },
                        oldSolution,
                        newSolution);
                    return false;
                }

                var solutionChanges = newSolution.GetChanges(oldSolution);
                this.CheckAllowedSolutionChanges(solutionChanges);

                var solutionWithLinkedFileChangesMerged = newSolution.WithMergedLinkedFileChangesAsync(oldSolution, solutionChanges, cancellationToken: CancellationToken.None).Result;
                solutionChanges = solutionWithLinkedFileChangesMerged.GetChanges(oldSolution);

                // added projects
                foreach (var proj in solutionChanges.GetAddedProjects())
                {
                    this.ApplyProjectAdded(CreateProjectInfo(proj));
                }

                // changed projects
                var projectChangesList = solutionChanges.GetProjectChanges().ToList();
                progressTracker.AddItems(projectChangesList.Count);

                foreach (var projectChanges in projectChangesList)
                {
                    this.ApplyProjectChanges(projectChanges);
                    progressTracker.ItemCompleted();
                }

                // changes in mapped files outside the workspace (may span multiple projects)
                this.ApplyMappedFileChanges(solutionChanges);

                // removed projects
                foreach (var proj in solutionChanges.GetRemovedProjects())
                {
                    this.ApplyProjectRemoved(proj.Id);
                }

                if (this.CurrentSolution.Options != newSolution.Options)
                {
                    _legacyOptions.SetOptions(newSolution.State.Options, newSolution.State.Options.GetChangedOptions());
                }

                if (!CurrentSolution.AnalyzerReferences.SequenceEqual(newSolution.AnalyzerReferences))
                {
                    foreach (var analyzerReference in solutionChanges.GetRemovedAnalyzerReferences())
                    {
                        ApplySolutionAnalyzerReferenceRemoved(analyzerReference);
                    }

                    foreach (var analyzerReference in solutionChanges.GetAddedAnalyzerReferences())
                    {
                        ApplySolutionAnalyzerReferenceAdded(analyzerReference);
                    }
                }

                return true;
            }
        }

        internal virtual void ApplyMappedFileChanges(SolutionChanges solutionChanges)
        {
            return;
        }

        private void CheckAllowedSolutionChanges(SolutionChanges solutionChanges)
        {
            // Note: For each kind of change first check if the change is disallowed and only if it is determine whether the change is actually made.
            // This is more efficient since most workspaces allow most changes and CanApplyChange is implementation is usually trivial.

            if (!CanApplyChange(ApplyChangesKind.RemoveProject) && solutionChanges.GetRemovedProjects().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Removing_projects_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.AddProject) && solutionChanges.GetAddedProjects().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Adding_projects_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.AddSolutionAnalyzerReference) && solutionChanges.GetAddedAnalyzerReferences().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Adding_analyzer_references_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.RemoveSolutionAnalyzerReference) && solutionChanges.GetRemovedAnalyzerReferences().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Removing_analyzer_references_is_not_supported);
            }

            foreach (var projectChanges in solutionChanges.GetProjectChanges())
            {
                CheckAllowedProjectChanges(projectChanges);
            }
        }

        private void CheckAllowedProjectChanges(ProjectChanges projectChanges)
        {
            // If CanApplyChange is true for ApplyChangesKind.ChangeCompilationOptions we allow any change to the compilaton options.
            // If only subset of changes is allowed CanApplyChange shall return false and CanApplyCompilationOptionChange
            // determines the outcome for the particular option change.
            if (!CanApplyChange(ApplyChangesKind.ChangeCompilationOptions) &&
                projectChanges.OldProject.CompilationOptions != projectChanges.NewProject.CompilationOptions)
            {
                // It's OK to assert this: if they were both null, the if check above would have been false right away
                // since they didn't change. Thus, at least one is non-null, and once you have a non-null CompilationOptions
                // and ParseOptions, we don't let you ever make it null again. Further, it can't ever start non-null:
                // we replace a null when a project is created with default compilation options.
                Contract.ThrowIfNull(projectChanges.OldProject.CompilationOptions);
                Contract.ThrowIfNull(projectChanges.NewProject.CompilationOptions);

                // The changes in CompilationOptions may include a change to the SyntaxTreeOptionsProvider, which would be happening
                // if an .editorconfig was added, removed, or modified. We'll compute the options without that change, and if there's
                // still changes then we need to verify we can apply those. The .editorconfig changes will also be represented as
                // document edits, which the host is expected to actually apply directly.
                var newOptionsWithoutSyntaxTreeOptionsChange =
                    projectChanges.NewProject.CompilationOptions.WithSyntaxTreeOptionsProvider(
                        projectChanges.OldProject.CompilationOptions.SyntaxTreeOptionsProvider);

                if (projectChanges.OldProject.CompilationOptions != newOptionsWithoutSyntaxTreeOptionsChange)
                {
                    // We're actually changing in a meaningful way, so now validate that the workspace can take it.
                    // We will pass into the CanApplyCompilationOptionChange newOptionsWithoutSyntaxTreeOptionsChange,
                    // which means it's only having to validate that the changes it's expected to apply are changing.
                    // The common pattern is to reject all changes not recognized, so this keeps existing code running just fine.
                    if (!CanApplyCompilationOptionChange(projectChanges.OldProject.CompilationOptions, newOptionsWithoutSyntaxTreeOptionsChange, projectChanges.NewProject))
                    {
                        throw new NotSupportedException(WorkspacesResources.Changing_compilation_options_is_not_supported);
                    }
                }
            }

            if (!CanApplyChange(ApplyChangesKind.ChangeParseOptions) &&
                projectChanges.OldProject.ParseOptions != projectChanges.NewProject.ParseOptions &&
                !CanApplyParseOptionChange(projectChanges.OldProject.ParseOptions!, projectChanges.NewProject.ParseOptions!, projectChanges.NewProject))
            {
                throw new NotSupportedException(WorkspacesResources.Changing_parse_options_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.AddDocument) && projectChanges.GetAddedDocuments().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Adding_documents_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.RemoveDocument) && projectChanges.GetRemovedDocuments().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Removing_documents_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.ChangeDocumentInfo)
                && projectChanges.GetChangedDocuments().Any(id => projectChanges.NewProject.GetDocument(id)!.HasInfoChanged(projectChanges.OldProject.GetDocument(id)!)))
            {
                throw new NotSupportedException(WorkspacesResources.Changing_document_property_is_not_supported);
            }

            var changedDocumentIds = projectChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true, IgnoreUnchangeableDocumentsWhenApplyingChanges).ToImmutableArray();

            if (!CanApplyChange(ApplyChangesKind.ChangeDocument) && changedDocumentIds.Length > 0)
            {
                throw new NotSupportedException(WorkspacesResources.Changing_documents_is_not_supported);
            }

            // Checking for unchangeable documents will only be done if we were asked not to ignore them.
            foreach (var documentId in changedDocumentIds)
            {
                var document = projectChanges.OldProject.State.DocumentStates.GetState(documentId) ??
                               projectChanges.NewProject.State.DocumentStates.GetState(documentId)!;

                if (!document.CanApplyChange())
                {
                    throw new NotSupportedException(string.Format(WorkspacesResources.Changing_document_0_is_not_supported, document.FilePath ?? document.Name));
                }
            }

            if (!CanApplyChange(ApplyChangesKind.AddAdditionalDocument) && projectChanges.GetAddedAdditionalDocuments().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Adding_additional_documents_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.RemoveAdditionalDocument) && projectChanges.GetRemovedAdditionalDocuments().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Removing_additional_documents_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.ChangeAdditionalDocument) && projectChanges.GetChangedAdditionalDocuments().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Changing_additional_documents_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.AddAnalyzerConfigDocument) && projectChanges.GetAddedAnalyzerConfigDocuments().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Adding_analyzer_config_documents_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.RemoveAnalyzerConfigDocument) && projectChanges.GetRemovedAnalyzerConfigDocuments().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Removing_analyzer_config_documents_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.ChangeAnalyzerConfigDocument) && projectChanges.GetChangedAnalyzerConfigDocuments().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Changing_analyzer_config_documents_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.AddProjectReference) && projectChanges.GetAddedProjectReferences().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Adding_project_references_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.RemoveProjectReference) && projectChanges.GetRemovedProjectReferences().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Removing_project_references_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.AddMetadataReference) && projectChanges.GetAddedMetadataReferences().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Adding_project_references_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.RemoveMetadataReference) && projectChanges.GetRemovedMetadataReferences().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Removing_project_references_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.AddAnalyzerReference) && projectChanges.GetAddedAnalyzerReferences().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Adding_analyzer_references_is_not_supported);
            }

            if (!CanApplyChange(ApplyChangesKind.RemoveAnalyzerReference) && projectChanges.GetRemovedAnalyzerReferences().Any())
            {
                throw new NotSupportedException(WorkspacesResources.Removing_analyzer_references_is_not_supported);
            }
        }

        /// <summary>
        /// Called during a call to <see cref="TryApplyChanges(Solution)"/> to determine if a specific change to <see cref="Project.CompilationOptions"/> is allowed.
        /// </summary>
        /// <remarks>
        /// This method is only called if <see cref="CanApplyChange" /> returns false for <see cref="ApplyChangesKind.ChangeCompilationOptions"/>.
        /// If <see cref="CanApplyChange" /> returns true, then that means all changes are allowed and this method does not need to be called.
        /// </remarks>
        /// <param name="oldOptions">The old <see cref="CompilationOptions"/> of the project from prior to the change.</param>
        /// <param name="newOptions">The new <see cref="CompilationOptions"/> of the project that was passed to <see cref="TryApplyChanges(Solution)"/>.</param>
        /// <param name="project">The project contained in the <see cref="Solution"/> passed to <see cref="TryApplyChanges(Solution)"/>.</param>
        public virtual bool CanApplyCompilationOptionChange(CompilationOptions oldOptions, CompilationOptions newOptions, Project project)
            => false;

        /// <summary>
        /// Called during a call to <see cref="TryApplyChanges(Solution)"/> to determine if a specific change to <see cref="Project.ParseOptions"/> is allowed.
        /// </summary>
        /// <remarks>
        /// This method is only called if <see cref="CanApplyChange" /> returns false for <see cref="ApplyChangesKind.ChangeParseOptions"/>.
        /// If <see cref="CanApplyChange" /> returns true, then that means all changes are allowed and this method does not need to be called.
        /// </remarks>
        /// <param name="oldOptions">The old <see cref="ParseOptions"/> of the project from prior to the change.</param>
        /// <param name="newOptions">The new <see cref="ParseOptions"/> of the project that was passed to <see cref="TryApplyChanges(Solution)"/>.</param>
        /// <param name="project">The project contained in the <see cref="Solution"/> passed to <see cref="TryApplyChanges(Solution)"/>.</param>
        public virtual bool CanApplyParseOptionChange(ParseOptions oldOptions, ParseOptions newOptions, Project project)
            => false;

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> for each project
        /// that has been added, removed or changed.
        ///
        /// Override this method if you want to modify how project changes are applied.
        /// </summary>
        protected virtual void ApplyProjectChanges(ProjectChanges projectChanges)
        {
            // It's OK to use the null-suppression operator when calling ApplyCompilation/ParseOptionsChanged: the only change that is allowed
            // is going from one non-null value to another which is blocked by the Project.WithCompilationOptions() API directly.

            // The changes in CompilationOptions may include a change to the SyntaxTreeOptionsProvider, which would be happening
            // if an .editorconfig was added, removed, or modified. We'll compute the options without that change, and if there's
            // still changes then we need to verify we can apply those. The .editorconfig changes will also be represented as
            // document edits, which the host is expected to actually apply directly.
            var newOptionsWithoutSyntaxTreeOptionsChange =
                projectChanges.NewProject.CompilationOptions?.WithSyntaxTreeOptionsProvider(
                    projectChanges.OldProject.CompilationOptions!.SyntaxTreeOptionsProvider);
            if (projectChanges.OldProject.CompilationOptions != newOptionsWithoutSyntaxTreeOptionsChange)
            {
                this.ApplyCompilationOptionsChanged(projectChanges.ProjectId, newOptionsWithoutSyntaxTreeOptionsChange!);
            }

            // changed parse options
            if (projectChanges.OldProject.ParseOptions != projectChanges.NewProject.ParseOptions)
            {
                this.ApplyParseOptionsChanged(projectChanges.ProjectId, projectChanges.NewProject.ParseOptions!);
            }

            // removed project references
            foreach (var removedProjectReference in projectChanges.GetRemovedProjectReferences())
            {
                this.ApplyProjectReferenceRemoved(projectChanges.ProjectId, removedProjectReference);
            }

            // added project references
            foreach (var addedProjectReference in projectChanges.GetAddedProjectReferences())
            {
                this.ApplyProjectReferenceAdded(projectChanges.ProjectId, addedProjectReference);
            }

            // removed metadata references
            foreach (var metadata in projectChanges.GetRemovedMetadataReferences())
            {
                this.ApplyMetadataReferenceRemoved(projectChanges.ProjectId, metadata);
            }

            // added metadata references
            foreach (var metadata in projectChanges.GetAddedMetadataReferences())
            {
                this.ApplyMetadataReferenceAdded(projectChanges.ProjectId, metadata);
            }

            // removed analyzer references
            foreach (var analyzerReference in projectChanges.GetRemovedAnalyzerReferences())
            {
                this.ApplyAnalyzerReferenceRemoved(projectChanges.ProjectId, analyzerReference);
            }

            // added analyzer references
            foreach (var analyzerReference in projectChanges.GetAddedAnalyzerReferences())
            {
                this.ApplyAnalyzerReferenceAdded(projectChanges.ProjectId, analyzerReference);
            }

            // removed documents
            foreach (var documentId in projectChanges.GetRemovedDocuments())
            {
                this.ApplyDocumentRemoved(documentId);
            }

            // removed additional documents
            foreach (var documentId in projectChanges.GetRemovedAdditionalDocuments())
            {
                this.ApplyAdditionalDocumentRemoved(documentId);
            }

            // removed analyzer config documents
            foreach (var documentId in projectChanges.GetRemovedAnalyzerConfigDocuments())
            {
                this.ApplyAnalyzerConfigDocumentRemoved(documentId);
            }

            // added documents
            foreach (var documentId in projectChanges.GetAddedDocuments())
            {
                var document = projectChanges.NewProject.GetDocument(documentId)!;
                var text = document.GetTextSynchronously(CancellationToken.None);
                var info = CreateDocumentInfoWithoutText(document);
                this.ApplyDocumentAdded(info, text);
            }

            // added additional documents
            foreach (var documentId in projectChanges.GetAddedAdditionalDocuments())
            {
                var document = projectChanges.NewProject.GetAdditionalDocument(documentId)!;
                var text = document.GetTextSynchronously(CancellationToken.None);
                var info = CreateDocumentInfoWithoutText(document);
                this.ApplyAdditionalDocumentAdded(info, text);
            }

            // added analyzer config documents
            foreach (var documentId in projectChanges.GetAddedAnalyzerConfigDocuments())
            {
                var document = projectChanges.NewProject.GetAnalyzerConfigDocument(documentId)!;
                var text = document.GetTextSynchronously(CancellationToken.None);
                var info = CreateDocumentInfoWithoutText(document);
                this.ApplyAnalyzerConfigDocumentAdded(info, text);
            }

            // changed documents
            foreach (var documentId in projectChanges.GetChangedDocuments())
            {
                ApplyChangedDocument(projectChanges, documentId);
            }

            // changed additional documents
            foreach (var documentId in projectChanges.GetChangedAdditionalDocuments())
            {
                var newDoc = projectChanges.NewProject.GetAdditionalDocument(documentId)!;

                // We don't understand the text of additional documents and so we just replace the entire text.
                var currentText = newDoc.GetTextSynchronously(CancellationToken.None); // needs wait
                this.ApplyAdditionalDocumentTextChanged(documentId, currentText);
            }

            // changed analyzer config documents
            foreach (var documentId in projectChanges.GetChangedAnalyzerConfigDocuments())
            {
                var newDoc = projectChanges.NewProject.GetAnalyzerConfigDocument(documentId)!;

                // We don't understand the text of analyzer config documents and so we just replace the entire text.
                var currentText = newDoc.GetTextSynchronously(CancellationToken.None); // needs wait
                this.ApplyAnalyzerConfigDocumentTextChanged(documentId, currentText);
            }
        }

        private void ApplyChangedDocument(
            ProjectChanges projectChanges, DocumentId documentId)
        {
            var oldDoc = projectChanges.OldProject.GetDocument(documentId)!;
            var newDoc = projectChanges.NewProject.GetDocument(documentId)!;

            // update text if it's changed (unless it's unchangeable and we were asked to exclude them)
            if (newDoc.HasTextChanged(oldDoc, IgnoreUnchangeableDocumentsWhenApplyingChanges))
            {
                // What we'd like to do here is figure out what actual text changes occurred and pass them on to the host.
                // However, since it is likely that the change was done by replacing the syntax tree, getting the actual text changes is non trivial.

                if (!oldDoc.TryGetText(out var oldText))
                {
                    // If we don't have easy access to the old text, then either it was never observed or it was kicked out of memory.
                    // Either way, the new text cannot possibly hold knowledge of the changes, and any new syntax tree will not likely be able to derive them.
                    // So just use whatever new text we have without preserving text changes.
                    var currentText = newDoc.GetTextSynchronously(CancellationToken.None); // needs wait
                    this.ApplyDocumentTextChanged(documentId, currentText);
                }
                else if (!newDoc.TryGetText(out var newText))
                {
                    // We have the old text, but no new text is easily available. This typically happens when the content is modified via changes to the syntax tree.
                    // Ask document to compute equivalent text changes by comparing the syntax trees, and use them to
                    var textChanges = newDoc.GetTextChangesAsync(oldDoc, CancellationToken.None).WaitAndGetResult_CanCallOnBackground(CancellationToken.None); // needs wait
                    this.ApplyDocumentTextChanged(documentId, oldText.WithChanges(textChanges));
                }
                else
                {
                    // We have both old and new text, so assume the text was changed manually.
                    // So either the new text already knows the individual changes or we do not have a way to compute them.
                    this.ApplyDocumentTextChanged(documentId, newText);
                }
            }

            // Update document info if changed. Updating the info can cause files to move on disk (or have other side effects),
            // so we do this after any text changes have been applied.
            if (newDoc.HasInfoChanged(oldDoc))
            {
                // ApplyDocumentInfoChanged ignores the loader information, so we can pass null for it
                ApplyDocumentInfoChanged(
                    documentId,
                    new DocumentInfo(newDoc.State.Attributes, loader: null, documentServiceProvider: newDoc.State.Services));
            }
        }

        [Conditional("DEBUG")]
        private static void CheckNoChanges(Solution oldSolution, Solution newSolution)
        {
            var changes = newSolution.GetChanges(oldSolution);
            Contract.ThrowIfTrue(changes.GetAddedProjects().Any());
            Contract.ThrowIfTrue(changes.GetRemovedProjects().Any());
            Contract.ThrowIfTrue(changes.GetProjectChanges().Any());
        }

        private static ProjectInfo CreateProjectInfo(Project project)
        {
            return ProjectInfo.Create(
                project.State.Attributes.With(version: VersionStamp.Create()),
                project.CompilationOptions,
                project.ParseOptions,
                project.Documents.Select(CreateDocumentInfoWithText),
                project.ProjectReferences,
                project.MetadataReferences,
                project.AnalyzerReferences,
                additionalDocuments: project.AdditionalDocuments.Select(CreateDocumentInfoWithText),
                analyzerConfigDocuments: project.AnalyzerConfigDocuments.Select(CreateDocumentInfoWithText),
                hostObjectType: project.State.HostObjectType);
        }

        private static DocumentInfo CreateDocumentInfoWithText(TextDocument doc)
            => CreateDocumentInfoWithoutText(doc).WithTextLoader(TextLoader.From(TextAndVersion.Create(doc.GetTextSynchronously(CancellationToken.None), VersionStamp.Create(), doc.FilePath)));

        internal static DocumentInfo CreateDocumentInfoWithoutText(TextDocument doc)
            => DocumentInfo.Create(
                doc.Id,
                doc.Name,
                doc.Folders,
                doc is Document sourceDoc ? sourceDoc.SourceCodeKind : SourceCodeKind.Regular,
                loader: null,
                filePath: doc.FilePath,
                isGenerated: doc.State.Attributes.IsGenerated)
                .WithDesignTimeOnly(doc.State.Attributes.DesignTimeOnly)
                .WithDocumentServiceProvider(doc.Services);

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to add a project to the current solution.
        ///
        /// Override this method to implement the capability of adding projects.
        /// </summary>
        protected virtual void ApplyProjectAdded(ProjectInfo project)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.AddProject));
            this.OnProjectAdded(project);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to remove a project from the current solution.
        ///
        /// Override this method to implement the capability of removing projects.
        /// </summary>
        protected virtual void ApplyProjectRemoved(ProjectId projectId)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.RemoveProject));
            this.OnProjectRemoved(projectId);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to change the compilation options.
        ///
        /// Override this method to implement the capability of changing compilation options.
        /// </summary>
        protected virtual void ApplyCompilationOptionsChanged(ProjectId projectId, CompilationOptions options)
        {
#if DEBUG
            var oldProject = CurrentSolution.GetRequiredProject(projectId);
            var newProjectForAssert = oldProject.WithCompilationOptions(options);

            Debug.Assert(CanApplyChange(ApplyChangesKind.ChangeCompilationOptions) ||
                         CanApplyCompilationOptionChange(oldProject.CompilationOptions!, options, newProjectForAssert));
#endif

            this.OnCompilationOptionsChanged(projectId, options);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to change the parse options.
        ///
        /// Override this method to implement the capability of changing parse options.
        /// </summary>
        protected virtual void ApplyParseOptionsChanged(ProjectId projectId, ParseOptions options)
        {
#if DEBUG
            var oldProject = CurrentSolution.GetRequiredProject(projectId);
            var newProjectForAssert = oldProject.WithParseOptions(options);

            Debug.Assert(CanApplyChange(ApplyChangesKind.ChangeParseOptions) ||
                         CanApplyParseOptionChange(oldProject.ParseOptions!, options, newProjectForAssert));
#endif
            this.OnParseOptionsChanged(projectId, options);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to add a project reference to a project.
        ///
        /// Override this method to implement the capability of adding project references.
        /// </summary>
        protected virtual void ApplyProjectReferenceAdded(ProjectId projectId, ProjectReference projectReference)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.AddProjectReference));
            this.OnProjectReferenceAdded(projectId, projectReference);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to remove a project reference from a project.
        ///
        /// Override this method to implement the capability of removing project references.
        /// </summary>
        protected virtual void ApplyProjectReferenceRemoved(ProjectId projectId, ProjectReference projectReference)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.RemoveProjectReference));
            this.OnProjectReferenceRemoved(projectId, projectReference);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to add a metadata reference to a project.
        ///
        /// Override this method to implement the capability of adding metadata references.
        /// </summary>
        protected virtual void ApplyMetadataReferenceAdded(ProjectId projectId, MetadataReference metadataReference)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.AddMetadataReference));
            this.OnMetadataReferenceAdded(projectId, metadataReference);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to remove a metadata reference from a project.
        ///
        /// Override this method to implement the capability of removing metadata references.
        /// </summary>
        protected virtual void ApplyMetadataReferenceRemoved(ProjectId projectId, MetadataReference metadataReference)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.RemoveMetadataReference));
            this.OnMetadataReferenceRemoved(projectId, metadataReference);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to add an analyzer reference to a project.
        ///
        /// Override this method to implement the capability of adding analyzer references.
        /// </summary>
        protected virtual void ApplyAnalyzerReferenceAdded(ProjectId projectId, AnalyzerReference analyzerReference)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.AddAnalyzerReference));
            this.OnAnalyzerReferenceAdded(projectId, analyzerReference);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to remove an analyzer reference from a project.
        ///
        /// Override this method to implement the capability of removing analyzer references.
        /// </summary>
        protected virtual void ApplyAnalyzerReferenceRemoved(ProjectId projectId, AnalyzerReference analyzerReference)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.RemoveAnalyzerReference));
            this.OnAnalyzerReferenceRemoved(projectId, analyzerReference);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to add an analyzer reference to the solution.
        ///
        /// Override this method to implement the capability of adding analyzer references.
        /// </summary>
        internal void ApplySolutionAnalyzerReferenceAdded(AnalyzerReference analyzerReference)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.AddSolutionAnalyzerReference));
            this.OnSolutionAnalyzerReferenceAdded(analyzerReference);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to remove an analyzer reference from the solution.
        ///
        /// Override this method to implement the capability of removing analyzer references.
        /// </summary>
        internal void ApplySolutionAnalyzerReferenceRemoved(AnalyzerReference analyzerReference)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.RemoveSolutionAnalyzerReference));
            this.OnSolutionAnalyzerReferenceRemoved(analyzerReference);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to add a new document to a project.
        ///
        /// Override this method to implement the capability of adding documents.
        /// </summary>
        protected virtual void ApplyDocumentAdded(DocumentInfo info, SourceText text)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.AddDocument));
            this.OnDocumentAdded(info.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create()))));
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to remove a document from a project.
        ///
        /// Override this method to implement the capability of removing documents.
        /// </summary>
        protected virtual void ApplyDocumentRemoved(DocumentId documentId)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.RemoveDocument));
            this.OnDocumentRemoved(documentId);
        }

        /// <summary>
        /// This method is called to change the text of a document.
        ///
        /// Override this method to implement the capability of changing document text.
        /// </summary>
        protected virtual void ApplyDocumentTextChanged(DocumentId id, SourceText text)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.ChangeDocument));
            this.OnDocumentTextChanged(id, text, PreservationMode.PreserveValue);
        }

        /// <summary>
        /// This method is called to change the info of a document.
        ///
        /// Override this method to implement the capability of changing a document's info.
        /// </summary>
        protected virtual void ApplyDocumentInfoChanged(DocumentId id, DocumentInfo info)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.ChangeDocumentInfo));
            this.OnDocumentInfoChanged(id, info);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to add a new additional document to a project.
        ///
        /// Override this method to implement the capability of adding additional documents.
        /// </summary>
        protected virtual void ApplyAdditionalDocumentAdded(DocumentInfo info, SourceText text)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.AddAdditionalDocument));
            this.OnAdditionalDocumentAdded(info.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create()))));
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to remove an additional document from a project.
        ///
        /// Override this method to implement the capability of removing additional documents.
        /// </summary>
        protected virtual void ApplyAdditionalDocumentRemoved(DocumentId documentId)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.RemoveAdditionalDocument));
            this.OnAdditionalDocumentRemoved(documentId);
        }

        /// <summary>
        /// This method is called to change the text of an additional document.
        ///
        /// Override this method to implement the capability of changing additional document text.
        /// </summary>
        protected virtual void ApplyAdditionalDocumentTextChanged(DocumentId id, SourceText text)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.ChangeAdditionalDocument));
            this.OnAdditionalDocumentTextChanged(id, text, PreservationMode.PreserveValue);
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to add a new analyzer config document to a project.
        ///
        /// Override this method to implement the capability of adding analyzer config documents.
        /// </summary>
        protected virtual void ApplyAnalyzerConfigDocumentAdded(DocumentInfo info, SourceText text)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.AddAnalyzerConfigDocument));
            this.OnAnalyzerConfigDocumentAdded(info.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create()))));
        }

        /// <summary>
        /// This method is called during <see cref="TryApplyChanges(Solution)"/> to remove an analyzer config document from a project.
        ///
        /// Override this method to implement the capability of removing analyzer config documents.
        /// </summary>
        protected virtual void ApplyAnalyzerConfigDocumentRemoved(DocumentId documentId)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.RemoveAnalyzerConfigDocument));
            this.OnAnalyzerConfigDocumentRemoved(documentId);
        }

        /// <summary>
        /// This method is called to change the text of an analyzer config document.
        ///
        /// Override this method to implement the capability of changing analyzer config document text.
        /// </summary>
        protected virtual void ApplyAnalyzerConfigDocumentTextChanged(DocumentId id, SourceText text)
        {
            Debug.Assert(CanApplyChange(ApplyChangesKind.ChangeAnalyzerConfigDocument));
            this.OnAnalyzerConfigDocumentTextLoaderChanged(id, TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create())));
        }

        #endregion

        #region Checks and Asserts
        /// <summary>
        /// Throws an exception is the solution is not empty.
        /// </summary>
        protected void CheckSolutionIsEmpty()
        {
            if (this.CurrentSolution.ProjectIds.Any())
            {
                throw new ArgumentException(WorkspacesResources.Workspace_is_not_empty);
            }
        }

        /// <summary>
        /// Throws an exception if the project is not part of the current solution.
        /// </summary>
        protected void CheckProjectIsInCurrentSolution(ProjectId projectId)
        {
            if (!this.CurrentSolution.ContainsProject(projectId))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_not_part_of_the_workspace,
                    this.GetProjectName(projectId)));
            }
        }

        /// <summary>
        /// Throws an exception is the project is part of the current solution.
        /// </summary>
        protected void CheckProjectIsNotInCurrentSolution(ProjectId projectId)
        {
            if (this.CurrentSolution.ContainsProject(projectId))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_already_part_of_the_workspace,
                    this.GetProjectName(projectId)));
            }
        }

        /// <summary>
        /// Throws an exception if a project does not have a specific project reference.
        /// </summary>
        protected void CheckProjectHasProjectReference(ProjectId fromProjectId, ProjectReference projectReference)
        {
            if (!this.CurrentSolution.GetProject(fromProjectId)!.ProjectReferences.Contains(projectReference))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_not_referenced,
                    this.GetProjectName(projectReference.ProjectId)));
            }
        }

        /// <summary>
        /// Throws an exception if a project already has a specific project reference.
        /// </summary>
        protected void CheckProjectDoesNotHaveProjectReference(ProjectId fromProjectId, ProjectReference projectReference)
        {
            if (this.CurrentSolution.GetProject(fromProjectId)!.ProjectReferences.Contains(projectReference))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_already_referenced,
                    this.GetProjectName(projectReference.ProjectId)));
            }
        }

        /// <summary>
        /// Throws an exception if project has a transitive reference to another project.
        /// </summary>
        protected void CheckProjectDoesNotHaveTransitiveProjectReference(ProjectId fromProjectId, ProjectId toProjectId)
        {
            var transitiveReferences = this.CurrentSolution.GetProjectDependencyGraph().GetProjectsThatThisProjectTransitivelyDependsOn(toProjectId);
            if (transitiveReferences.Contains(fromProjectId))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources.Adding_project_reference_from_0_to_1_will_cause_a_circular_reference,
                    this.GetProjectName(fromProjectId), this.GetProjectName(toProjectId)));
            }
        }

        /// <summary>
        /// Throws an exception if a project does not have a specific metadata reference.
        /// </summary>
        protected void CheckProjectHasMetadataReference(ProjectId projectId, MetadataReference metadataReference)
        {
            if (!this.CurrentSolution.GetProject(projectId)!.MetadataReferences.Contains(metadataReference))
            {
                throw new ArgumentException(WorkspacesResources.Metadata_is_not_referenced);
            }
        }

        /// <summary>
        /// Throws an exception if a project already has a specific metadata reference.
        /// </summary>
        protected void CheckProjectDoesNotHaveMetadataReference(ProjectId projectId, MetadataReference metadataReference)
        {
            if (this.CurrentSolution.GetProject(projectId)!.MetadataReferences.Contains(metadataReference))
            {
                throw new ArgumentException(WorkspacesResources.Metadata_is_already_referenced);
            }
        }

        /// <summary>
        /// Throws an exception if a project does not have a specific analyzer reference.
        /// </summary>
        protected void CheckProjectHasAnalyzerReference(ProjectId projectId, AnalyzerReference analyzerReference)
        {
            if (!this.CurrentSolution.GetProject(projectId)!.AnalyzerReferences.Contains(analyzerReference))
            {
                throw new ArgumentException(string.Format(WorkspacesResources._0_is_not_present, analyzerReference));
            }
        }

        /// <summary>
        /// Throws an exception if a project already has a specific analyzer reference.
        /// </summary>
        protected void CheckProjectDoesNotHaveAnalyzerReference(ProjectId projectId, AnalyzerReference analyzerReference)
        {
            if (this.CurrentSolution.GetProject(projectId)!.AnalyzerReferences.Contains(analyzerReference))
            {
                throw new ArgumentException(string.Format(WorkspacesResources._0_is_already_present, analyzerReference));
            }
        }

        /// <summary>
        /// Throws an exception if a project already has a specific analyzer reference.
        /// </summary>
        internal static void CheckSolutionHasAnalyzerReference(Solution solution, AnalyzerReference analyzerReference)
        {
            if (!solution.AnalyzerReferences.Contains(analyzerReference))
            {
                throw new ArgumentException(string.Format(WorkspacesResources._0_is_not_present, analyzerReference));
            }
        }

        /// <summary>
        /// Throws an exception if a project already has a specific analyzer reference.
        /// </summary>
        internal static void CheckSolutionDoesNotHaveAnalyzerReference(Solution solution, AnalyzerReference analyzerReference)
        {
            if (solution.AnalyzerReferences.Contains(analyzerReference))
            {
                throw new ArgumentException(string.Format(WorkspacesResources._0_is_already_present, analyzerReference));
            }
        }

        /// <summary>
        /// Throws an exception if a document is not part of the current solution.
        /// </summary>
        protected void CheckDocumentIsInCurrentSolution(DocumentId documentId)
        {
            if (this.CurrentSolution.GetDocument(documentId) == null)
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_not_part_of_the_workspace,
                    this.GetDocumentName(documentId)));
            }
        }

        /// <summary>
        /// Throws an exception if an additional document is not part of the current solution.
        /// </summary>
        protected void CheckAdditionalDocumentIsInCurrentSolution(DocumentId documentId)
        {
            if (this.CurrentSolution.GetAdditionalDocument(documentId) == null)
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_not_part_of_the_workspace,
                    this.GetDocumentName(documentId)));
            }
        }

        /// <summary>
        /// Throws an exception if an analyzer config is not part of the current solution.
        /// </summary>
        protected void CheckAnalyzerConfigDocumentIsInCurrentSolution(DocumentId documentId)
        {
            if (!this.CurrentSolution.ContainsAnalyzerConfigDocument(documentId))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_not_part_of_the_workspace,
                    this.GetDocumentName(documentId)));
            }
        }

        /// <summary>
        /// Throws an exception if a document is already part of the current solution.
        /// </summary>
        protected void CheckDocumentIsNotInCurrentSolution(DocumentId documentId)
        {
            if (this.CurrentSolution.ContainsDocument(documentId))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_already_part_of_the_workspace,
                    this.GetDocumentName(documentId)));
            }
        }

        /// <summary>
        /// Throws an exception if an additional document is already part of the current solution.
        /// </summary>
        protected void CheckAdditionalDocumentIsNotInCurrentSolution(DocumentId documentId)
        {
            if (this.CurrentSolution.ContainsAdditionalDocument(documentId))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_already_part_of_the_workspace,
                    this.GetAdditionalDocumentName(documentId)));
            }
        }

        /// <summary>
        /// Throws an exception if the analyzer config document is already part of the current solution.
        /// </summary>
        protected void CheckAnalyzerConfigDocumentIsNotInCurrentSolution(DocumentId documentId)
        {
            if (this.CurrentSolution.ContainsAnalyzerConfigDocument(documentId))
            {
                throw new ArgumentException(string.Format(
                    WorkspacesResources._0_is_already_part_of_the_workspace,
                    this.GetAnalyzerConfigDocumentName(documentId)));
            }
        }

        /// <summary>
        /// Gets the name to use for a project in an error message.
        /// </summary>
        protected virtual string GetProjectName(ProjectId projectId)
        {
            var project = this.CurrentSolution.GetProject(projectId);
            var name = project != null ? project.Name : "<Project" + projectId.Id + ">";
            return name;
        }

        /// <summary>
        /// Gets the name to use for a document in an error message.
        /// </summary>
        protected virtual string GetDocumentName(DocumentId documentId)
        {
            var document = this.CurrentSolution.GetTextDocument(documentId);
            var name = document != null ? document.Name : "<Document" + documentId.Id + ">";
            return name;
        }

        /// <summary>
        /// Gets the name to use for an additional document in an error message.
        /// </summary>
        protected virtual string GetAdditionalDocumentName(DocumentId documentId)
            => GetDocumentName(documentId);

        /// <summary>
        /// Gets the name to use for an analyzer document in an error message.
        /// </summary>
        protected virtual string GetAnalyzerConfigDocumentName(DocumentId documentId)
            => GetDocumentName(documentId);

        #endregion
    }
}
