﻿' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Collections.Immutable
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports Microsoft.CodeAnalysis.Test.Utilities
Imports Microsoft.CodeAnalysis.Text

Namespace Global.Microsoft.CodeAnalysis.VisualBasic

    Friend Module DiagnosticsExtensions

        <Extension>
        Friend Function VerifyDiagnostics(c As VisualBasicCompilation, ParamArray expected As DiagnosticDescription()) As VisualBasicCompilation
            Dim diagnostics = c.GetDiagnostics(CompilationStage.Compile)
            diagnostics.Verify(expected)
            Return c
        End Function

        <Extension>
        Friend Function GetDiagnostics(c As VisualBasicCompilation, stage As CompilationStage) As ImmutableArray(Of Diagnostic)
            Return c.GetDiagnostics(stage, includeEarlierStages:=True, includeDiagnosticsWithSourceSuppression:=False, cancellationToken:=CancellationToken.None)
        End Function

        <Extension>
        Friend Function GetDiagnosticsForSyntaxTree(c As VisualBasicCompilation, stage As CompilationStage, tree As SyntaxTree, Optional filterSpan As TextSpan? = Nothing) As ImmutableArray(Of Diagnostic)
            Return c.GetDiagnosticsForSyntaxTree(stage, tree, filterSpan, includeEarlierStages:=True, includeDiagnosticsWithSourceSuppression:=False, cancellationToken:=CancellationToken.None)
        End Function

        ' TODO: Figure out how to return a localized message using VB
        '<Extension()>
        'Public Function ToLocalizedString(id As MessageID) As String
        '    Return New LocalizableErrorArgument(id).ToString(Nothing, Nothing)
        'End Function
    End Module

End Namespace
