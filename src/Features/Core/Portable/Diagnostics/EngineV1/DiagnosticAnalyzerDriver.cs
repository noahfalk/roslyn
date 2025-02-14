﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics.Log;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using static Microsoft.CodeAnalysis.Diagnostics.Telemetry.AnalyzerTelemetry;
using Microsoft.CodeAnalysis.ErrorReporting;

namespace Microsoft.CodeAnalysis.Diagnostics.EngineV1
{
    internal class DiagnosticAnalyzerDriver
    {
        private readonly Document _document;

        // The root of the document.  May be null for documents without a root.
        private readonly SyntaxNode _root;

        // The span of the documents we want diagnostics for.  If null, then we want diagnostics 
        // for the entire file.
        private readonly TextSpan? _span;
        private readonly Project _project;
        private readonly DiagnosticIncrementalAnalyzer _owner;
        private readonly CancellationToken _cancellationToken;
        private readonly CompilationWithAnalyzersOptions _analysisOptions;

        private CompilationWithAnalyzers _lazyCompilationWithAnalyzers;

        public DiagnosticAnalyzerDriver(
            Document document,
            TextSpan? span,
            SyntaxNode root,
            DiagnosticIncrementalAnalyzer owner,
            CancellationToken cancellationToken)
            : this(document.Project, owner, cancellationToken)
        {
            _document = document;
            _span = span;
            _root = root;
        }

        public DiagnosticAnalyzerDriver(
            Project project,
            DiagnosticIncrementalAnalyzer owner,
            CancellationToken cancellationToken)
        {
            _project = project;
            _owner = owner;
            _cancellationToken = cancellationToken;
            _analysisOptions = new CompilationWithAnalyzersOptions(
                new WorkspaceAnalyzerOptions(project.AnalyzerOptions, project.Solution.Workspace),
                owner.GetOnAnalyzerException(project.Id),
                concurrentAnalysis: false,
                logAnalyzerExecutionTime: true);
            _lazyCompilationWithAnalyzers = null;
        }

        public Document Document
        {
            get
            {
                Contract.ThrowIfNull(_document);
                return _document;
            }
        }

        public TextSpan? Span
        {
            get
            {
                Contract.ThrowIfNull(_document);
                return _span;
            }
        }

        public Project Project
        {
            get
            {
                Contract.ThrowIfNull(_project);
                return _project;
            }
        }

        public CancellationToken CancellationToken
        {
            get
            {
                return _cancellationToken;
            }
        }

        private CompilationWithAnalyzers GetCompilationWithAnalyzers(Compilation compilation)
        {
            Contract.ThrowIfFalse(_project.SupportsCompilation);

            if (_lazyCompilationWithAnalyzers == null)
            {
                var analyzers = _owner
                        .GetAnalyzers(_project)
                        .Where(a => !CompilationWithAnalyzers.IsDiagnosticAnalyzerSuppressed(a, compilation.Options, _analysisOptions.OnAnalyzerException))
                        .ToImmutableArray()
                        .Distinct();
                Interlocked.CompareExchange(ref _lazyCompilationWithAnalyzers, new CompilationWithAnalyzers(compilation, analyzers, _analysisOptions), null);
            }

            return _lazyCompilationWithAnalyzers;
        }

        public async Task<ActionCounts> GetAnalyzerActionsAsync(DiagnosticAnalyzer analyzer)
        {
            try
            {
                var compilation = await _project.GetCompilationAsync(_cancellationToken).ConfigureAwait(false);
                var compWithAnalyzers = this.GetCompilationWithAnalyzers(compilation);
                return await compWithAnalyzers.GetAnalyzerActionCountsAsync(analyzer, _cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        public async Task<ImmutableArray<Diagnostic>> GetSyntaxDiagnosticsAsync(DiagnosticAnalyzer analyzer)
        {
            try
            {
                var compilation = _document.Project.SupportsCompilation ? await _document.Project.GetCompilationAsync(_cancellationToken).ConfigureAwait(false) : null;

                Contract.ThrowIfNull(_document);

                var documentAnalyzer = analyzer as DocumentDiagnosticAnalyzer;
                if (documentAnalyzer != null)
                {
                    using (var pooledObject = SharedPools.Default<List<Diagnostic>>().GetPooledObject())
                    {
                        var diagnostics = pooledObject.Object;
                        _cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            await documentAnalyzer.AnalyzeSyntaxAsync(_document, diagnostics.Add, _cancellationToken).ConfigureAwait(false);
                            return GetFilteredDocumentDiagnostics(diagnostics, compilation).ToImmutableArray();
                        }
                        catch (Exception e) when (!IsCanceled(e, _cancellationToken))
                        {
                            OnAnalyzerException(e, analyzer, compilation);
                            return ImmutableArray<Diagnostic>.Empty;
                        }
                    }
                }

                if (!_document.SupportsSyntaxTree)
                {
                    return ImmutableArray<Diagnostic>.Empty;
                }

                var compilationWithAnalyzers = GetCompilationWithAnalyzers(compilation);
                var syntaxDiagnostics = await compilationWithAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(_root.SyntaxTree, ImmutableArray.Create(analyzer), _cancellationToken).ConfigureAwait(false);
                await UpdateAnalyzerTelemetryDataAsync(analyzer, compilationWithAnalyzers).ConfigureAwait(false);
                return GetFilteredDocumentDiagnostics(syntaxDiagnostics, compilation, onlyLocationFiltering: true).ToImmutableArray();
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        private IEnumerable<Diagnostic> GetFilteredDocumentDiagnostics(IEnumerable<Diagnostic> diagnostics, Compilation compilation, bool onlyLocationFiltering = false)
        {
            if (_root == null)
            {
                return diagnostics;
            }

            return GetFilteredDocumentDiagnosticsCore(diagnostics, compilation, onlyLocationFiltering);
        }

        private IEnumerable<Diagnostic> GetFilteredDocumentDiagnosticsCore(IEnumerable<Diagnostic> diagnostics, Compilation compilation, bool onlyLocationFiltering)
        {
            var diagsFilteredByLocation = diagnostics.Where(diagnostic => (diagnostic.Location == Location.None) ||
                        (diagnostic.Location.SourceTree == _root.SyntaxTree &&
                         (_span == null || diagnostic.Location.SourceSpan.IntersectsWith(_span.Value))));

            return compilation == null || onlyLocationFiltering
                ? diagsFilteredByLocation
                : CompilationWithAnalyzers.GetEffectiveDiagnostics(diagsFilteredByLocation, compilation);
        }

        internal void OnAnalyzerException(Exception ex, DiagnosticAnalyzer analyzer, Compilation compilation)
        {
            var exceptionDiagnostic = AnalyzerHelper.CreateAnalyzerExceptionDiagnostic(analyzer, ex);

            if (compilation != null)
            {
                exceptionDiagnostic = CompilationWithAnalyzers.GetEffectiveDiagnostics(ImmutableArray.Create(exceptionDiagnostic), compilation).SingleOrDefault();
            }

            _analysisOptions.OnAnalyzerException(ex, analyzer, exceptionDiagnostic);
        }

        public async Task<ImmutableArray<Diagnostic>> GetSemanticDiagnosticsAsync(DiagnosticAnalyzer analyzer)
        {
            try
            {
                var model = await _document.GetSemanticModelAsync(_cancellationToken).ConfigureAwait(false);
                var compilation = model?.Compilation;

                Contract.ThrowIfNull(_document);

                var documentAnalyzer = analyzer as DocumentDiagnosticAnalyzer;
                if (documentAnalyzer != null)
                {
                    using (var pooledObject = SharedPools.Default<List<Diagnostic>>().GetPooledObject())
                    {
                        var diagnostics = pooledObject.Object;
                        _cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            await documentAnalyzer.AnalyzeSemanticsAsync(_document, diagnostics.Add, _cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception e) when (!IsCanceled(e, _cancellationToken))
                        {
                            OnAnalyzerException(e, analyzer, compilation);
                            return ImmutableArray<Diagnostic>.Empty;
                        }

                        return GetFilteredDocumentDiagnostics(diagnostics, compilation).ToImmutableArray();
                    }
                }

                if (!_document.SupportsSyntaxTree)
                {
                    return ImmutableArray<Diagnostic>.Empty;
                }

                var compilationWithAnalyzers = GetCompilationWithAnalyzers(compilation);
                var semanticDiagnostics = await compilationWithAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, _span, ImmutableArray.Create(analyzer), _cancellationToken).ConfigureAwait(false);
                await UpdateAnalyzerTelemetryDataAsync(analyzer, compilationWithAnalyzers).ConfigureAwait(false);
                return semanticDiagnostics;
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        public async Task<ImmutableArray<Diagnostic>> GetProjectDiagnosticsAsync(DiagnosticAnalyzer analyzer)
        {
            try
            {
                Contract.ThrowIfNull(_project);
                Contract.ThrowIfFalse(_document == null);

                using (var diagnostics = SharedPools.Default<List<Diagnostic>>().GetPooledObject())
                {
                    if (_project.SupportsCompilation)
                    {
                        await this.GetCompilationDiagnosticsAsync(analyzer, diagnostics.Object).ConfigureAwait(false);
                    }

                    await this.GetProjectDiagnosticsWorkerAsync(analyzer, diagnostics.Object).ConfigureAwait(false);

                    return diagnostics.Object.ToImmutableArray();
                }
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        private async Task GetProjectDiagnosticsWorkerAsync(DiagnosticAnalyzer analyzer, List<Diagnostic> diagnostics)
        {
            try
            {
                var projectAnalyzer = analyzer as ProjectDiagnosticAnalyzer;
                if (projectAnalyzer == null)
                {
                    return;
                }

                try
                {
                    await projectAnalyzer.AnalyzeProjectAsync(_project, diagnostics.Add, _cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (!IsCanceled(e, _cancellationToken))
                {
                    var compilation = await _project.GetCompilationAsync(_cancellationToken).ConfigureAwait(false);
                    OnAnalyzerException(e, analyzer, compilation);
                }
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        private async Task GetCompilationDiagnosticsAsync(DiagnosticAnalyzer analyzer, List<Diagnostic> diagnostics)
        {
            try
            {
                Contract.ThrowIfFalse(_project.SupportsCompilation);

                var compilation = await _project.GetCompilationAsync(_cancellationToken).ConfigureAwait(false);
                var compilationWithAnalyzers = GetCompilationWithAnalyzers(compilation);
                var compilationDiagnostics = await compilationWithAnalyzers.GetAnalyzerCompilationDiagnosticsAsync(ImmutableArray.Create(analyzer), _cancellationToken).ConfigureAwait(false);
                await UpdateAnalyzerTelemetryDataAsync(analyzer, compilationWithAnalyzers).ConfigureAwait(false);
                diagnostics.AddRange(compilationDiagnostics);
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        private async Task UpdateAnalyzerTelemetryDataAsync(DiagnosticAnalyzer analyzer, CompilationWithAnalyzers compilationWithAnalyzers)
        {
            try
            {
                var actionCounts = await compilationWithAnalyzers.GetAnalyzerActionCountsAsync(analyzer, _cancellationToken).ConfigureAwait(false);
                DiagnosticAnalyzerLogger.UpdateAnalyzerTypeCount(analyzer, actionCounts, _project, _owner.DiagnosticLogAggregator);
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        private static bool IsCanceled(Exception ex, CancellationToken cancellationToken)
        {
            return (ex as OperationCanceledException)?.CancellationToken == cancellationToken;
        }
    }
}
