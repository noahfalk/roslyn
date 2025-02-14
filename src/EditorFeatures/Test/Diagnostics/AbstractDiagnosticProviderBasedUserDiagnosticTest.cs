// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.UnitTests.Diagnostics;
using Roslyn.Utilities;
using Xunit;
using System.Collections.Concurrent;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Diagnostics
{
    public abstract class AbstractDiagnosticProviderBasedUserDiagnosticTest : AbstractUserDiagnosticTest
    {
        private readonly ConcurrentDictionary<Workspace, Tuple<DiagnosticAnalyzer, CodeFixProvider>> _analyzerAndFixerMap =
            new ConcurrentDictionary<Workspace, Tuple<DiagnosticAnalyzer, CodeFixProvider>>();

        internal abstract Tuple<DiagnosticAnalyzer, CodeFixProvider> CreateDiagnosticProviderAndFixer(Workspace workspace);

        private Tuple<DiagnosticAnalyzer, CodeFixProvider> GetOrCreateDiagnosticProviderAndFixer(Workspace workspace)
        {
            return _analyzerAndFixerMap.GetOrAdd(workspace, CreateDiagnosticProviderAndFixer);
        }

        internal override IEnumerable<Diagnostic> GetDiagnostics(TestWorkspace workspace)
        {
            var providerAndFixer = GetOrCreateDiagnosticProviderAndFixer(workspace);

            var provider = providerAndFixer.Item1;
            TextSpan span;
            var document = GetDocumentAndSelectSpan(workspace, out span);
            return DiagnosticProviderTestUtilities.GetAllDiagnostics(provider, document, span);
        }

        internal override IEnumerable<Tuple<Diagnostic, CodeFixCollection>> GetDiagnosticAndFixes(TestWorkspace workspace, string fixAllActionId)
        {
            var providerAndFixer = GetOrCreateDiagnosticProviderAndFixer(workspace);

            var provider = providerAndFixer.Item1;
            Document document;
            TextSpan span;
            string annotation = null;
            if (!TryGetDocumentAndSelectSpan(workspace, out document, out span))
            {
                document = GetDocumentAndAnnotatedSpan(workspace, out annotation, out span);
            }

            using (var testDriver = new TestDiagnosticAnalyzerDriver(document.Project, provider))
            {
                var diagnostics = testDriver.GetAllDiagnostics(provider, document, span);
                var fixer = providerAndFixer.Item2;
                var ids = new HashSet<string>(fixer.FixableDiagnosticIds);
                var dxs = diagnostics.Where(d => ids.Contains(d.Id)).ToList();
                return GetDiagnosticAndFixes(dxs, provider, fixer, testDriver, document, span, annotation, fixAllActionId);
            }
        }
    }
}
