﻿
Imports Microsoft.CodeAnalysis.CodeFixes
Imports Microsoft.CodeAnalysis.Diagnostics

Namespace Microsoft.CodeAnalysis.Editor.UnitTests.Diagnostics.UseAutoProperty
    Public Class UseAutoPropertyTests
        Inherits AbstractCrossLanguageUserDiagnosticTest

        Friend Overrides Function CreateDiagnosticProviderAndFixer(workspace As Workspace, language As String) As Tuple(Of DiagnosticAnalyzer, CodeFixProvider)
            If language = LanguageNames.CSharp Then
                Return New Tuple(Of DiagnosticAnalyzer, CodeFixProvider)(New CSharp.UseAutoProperty.UseAutoPropertyAnalyzer(), New CSharp.UseAutoProperty.UseAutoPropertyCodeFixProvider())
            ElseIf language = LanguageNames.VisualBasic
                Return New Tuple(Of DiagnosticAnalyzer, CodeFixProvider)(New VisualBasic.UseAutoProperty.UseAutoPropertyAnalyzer(), New VisualBasic.UseAutoProperty.UseAutoPropertyCodeFixProvider())
            Else
                Throw New Exception()
            End If
        End Function

        <Fact(), Trait(Traits.Feature, Traits.Features.CodeActionsUseAutoProperty)>
        Public Sub TestMultiFile_CSharp()
            Dim input =
                <Workspace>
                    <Project Language='C#' AssemblyName='CSharpAssembly1' CommonReferences='true'>
                        <Document FilePath='Test1.cs'>
partial class C
{
    int $$i;
}
                        </Document>
                        <Document FilePath='Test2.cs'>
partial class C
{
    int P { get { return i; } }
}
                        </Document>
                    </Project>
                </Workspace>

            Test(input, fileNameToExpected:=
                 New Dictionary(Of String, String) From {
                    {"Test1.cs",
<text>
partial class C
{
}
</text>.Value.Trim()},
                    {"Test2.cs",
<text>
partial class C
{
    int P { get; }
}
</text>.Value.Trim()}
                })
        End Sub

        <Fact(), Trait(Traits.Feature, Traits.Features.CodeActionsUseAutoProperty)>
        Public Sub TestMultiFile_VisualBasic()
            Dim input =
                <Workspace>
                    <Project Language='Visual Basic' AssemblyName='CSharpAssembly1' CommonReferences='true'>
                        <Document FilePath='Test1.vb'>
partial class C
    dim $$i as Integer
end class
                        </Document>
                        <Document FilePath='Test2.vb'>
partial class C
    property P as Integer
        get
            return i
        end get
    end property
end class
                        </Document>
                    </Project>
                </Workspace>

            Test(input, fileNameToExpected:=
                 New Dictionary(Of String, String) From {
                    {"Test1.vb",
<text>
partial class C
end class
</text>.Value.Trim()},
                    {"Test2.vb",
<text>
partial class C
    ReadOnly property P as Integer
end class
</text>.Value.Trim()}
                })
        End Sub
    End Class
End Namespace