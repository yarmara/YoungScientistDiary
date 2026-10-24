// SPDX-License-Identifier: Apache-2.0
using YoungScientistDiary.SelfTests;

var failed = false;
foreach (var (name, test) in new (string, Action)[]
{
    ("SQLite, revisions, managed files, backup and restore", StorageTests.Run),
    ("CSV/XLSX and verified statistics", AnalysisTests.Run),
    ("Bibliography imports, CSL formatting and document exports", LiteratureTests.Run),
    ("Project-to-analysis-to-bibliography backup and recovery workflow", WorkflowTests.Run)
})
{
    try { test(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failed = true; Console.Error.WriteLine("FAIL: " + name + "\n" + ex); }
}
return failed ? 1 : 0;
