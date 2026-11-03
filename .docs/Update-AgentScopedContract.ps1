# Synchronize the additive contract without changing any existing worksheet.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$contractLines = [IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'Agent_Scoped_Planning_Ver2.0.md'))
foreach ($bookName in @('ANGI_API_Design_Ver1.8.xlsx', 'ANGI_Data_Dictionary_Ver1.2.xlsx')) {
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $PSScriptRoot $bookName), [IO.Compression.ZipArchiveMode]::Update)
    try {
        function Read-Entry([string]$name) {
            $reader = [IO.StreamReader]::new($archive.GetEntry($name).Open())
            try { $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        function Write-Entry([string]$name, [string]$value) {
            $oldEntry = $archive.GetEntry($name)
            if ($oldEntry) { $oldEntry.Delete() }
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($value) } finally { $writer.Dispose() }
        }
        [xml]$workbook = Read-Entry 'xl/workbook.xml'
        $rows = for ($index = 0; $index -lt $contractLines.Length; $index++) {
            $number = $index + 1
            $escaped = [Security.SecurityElement]::Escape($contractLines[$index])
            '<row r="' + $number + '"><c r="A' + $number + '" t="inlineStr"><is><t xml:space="preserve">' + $escaped + '</t></is></c></row>'
        }
        Write-Entry 'xl/worksheets/agent-scoped-v2.xml' ('<?xml version="1.0" encoding="utf-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cols><col min="1" max="1" width="140" customWidth="1"/></cols><sheetData>' + ($rows -join '') + '</sheetData></worksheet>')
        if (@($workbook.workbook.sheets.sheet | Where-Object name -eq 'Agent Scoped v2').Count -gt 0) { continue }
        [xml]$relationships = Read-Entry 'xl/_rels/workbook.xml.rels'
        [xml]$contentTypes = Read-Entry '[Content_Types].xml'
        $sheetId = 1 + ($workbook.workbook.sheets.sheet | ForEach-Object { [int]$_.sheetId } | Measure-Object -Maximum).Maximum
        $relationshipId = 'rIdAgentScopedV2'
        $sheet = $workbook.CreateElement('sheet', $workbook.DocumentElement.NamespaceURI)
        $sheet.SetAttribute('name', 'Agent Scoped v2'); $sheet.SetAttribute('sheetId', [string]$sheetId)
        $null = $sheet.SetAttribute('id', 'http://schemas.openxmlformats.org/officeDocument/2006/relationships', $relationshipId)
        $null = $workbook.workbook.sheets.AppendChild($sheet)
        $relationship = $relationships.CreateElement('Relationship', $relationships.DocumentElement.NamespaceURI)
        $relationship.SetAttribute('Id', $relationshipId)
        $relationship.SetAttribute('Type', 'http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet')
        $relationship.SetAttribute('Target', "worksheets/agent-scoped-v2.xml")
        $null = $relationships.DocumentElement.AppendChild($relationship)
        $override = $contentTypes.CreateElement('Override', $contentTypes.DocumentElement.NamespaceURI)
        $override.SetAttribute('PartName', '/xl/worksheets/agent-scoped-v2.xml')
        $override.SetAttribute('ContentType', 'application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml')
        $null = $contentTypes.DocumentElement.AppendChild($override)
        Write-Entry 'xl/workbook.xml' $workbook.OuterXml
        Write-Entry 'xl/_rels/workbook.xml.rels' $relationships.OuterXml
        Write-Entry '[Content_Types].xml' $contentTypes.OuterXml
    } finally { $archive.Dispose() }
}
