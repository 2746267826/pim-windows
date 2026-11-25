#!/usr/bin/env node
/**
 * Regression tests for the daemon-only Windows release payload.
 *
 * The tests exercise the same manifest validator used by build-windows.yml and also
 * assert that the workflow does not publish the retired shell or reuse a dirty publish
 * directory. A negative fixture for each retired dependency keeps the checker from
 * becoming a "looks for files that happen to exist" no-op.
 */
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { writeZipFromDirectory } from './zip-writer.mjs'
import {
  compareDirectoryAndZip,
  readDirectoryManifest,
  readZipManifest,
  validateWindowsPayloadEntries,
} from './verify-windows-payload.mjs'

const REQUIRED_FILES = [
  'Pim.Client.App.exe',
  'KeyStats.exe',
  'fix-keystats-session.ps1',
  'VERSION',
  'browser-extension/manifest.json',
  'browser-extension-firefox/manifest.json',
  'browser-extension-site/manifest.json',
  'browser-extension-site-firefox/manifest.json',
]

function makePayload() {
  const root = mkdtempSync(join(tmpdir(), 'pim-windows-payload-'))
  for (const relativePath of REQUIRED_FILES) {
    const fullPath = join(root, ...relativePath.split('/'))
    mkdirSync(dirname(fullPath), { recursive: true })
    writeFileSync(fullPath, `fixture:${relativePath}`)
  }
  return root
}

test('clean daemon payload passes the manifest', () => {
  const result = validateWindowsPayloadEntries(REQUIRED_FILES)
  assert.deepEqual(result.errors, [])
  assert.deepEqual(result.entries, [...REQUIRED_FILES].sort())
})

for (const retired of [
  'Pim.Shell.App.exe',
  'Microsoft.Web.WebView2.Core.dll',
  'Microsoft.Web.WebView2.Wpf.dll',
  'WebView2Loader.dll',
  'runtimes/win-x64/native/WebView2Loader.dll',
]) {
  test(`retired payload is rejected: ${retired}`, () => {
    const result = validateWindowsPayloadEntries([...REQUIRED_FILES, retired])
    assert.ok(
      result.errors.some((error) => error.includes(retired)),
      `expected ${retired} to be rejected, got: ${result.errors.join('; ')}`,
    )
  })
}

test('missing required files fail instead of producing a green partial payload', () => {
  const result = validateWindowsPayloadEntries(REQUIRED_FILES.filter((entry) => entry !== 'KeyStats.exe'))
  assert.ok(result.errors.some((error) => error.includes('KeyStats.exe')))
})

test('staging directory and ZIP both reject a retired shell payload', () => {
  const root = makePayload()
  const zipDir = mkdtempSync(join(tmpdir(), 'pim-windows-payload-zip-'))
  const zipPath = join(zipDir, 'pim-windows.zip')
  try {
    writeFileSync(join(root, 'Pim.Shell.App.exe'), 'retired')
    const directoryManifest = readDirectoryManifest(root)
    assert.ok(directoryManifest.errors.some((error) => error.includes('Pim.Shell.App.exe')))

    writeZipFromDirectory(root, zipPath)
    const zipManifest = readZipManifest(zipPath)
    assert.ok(zipManifest.errors.some((error) => error.includes('Pim.Shell.App.exe')))
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zipDir, { recursive: true, force: true })
  }
})

test('clean staging directory and ZIP match exactly', () => {
  const root = makePayload()
  const zipDir = mkdtempSync(join(tmpdir(), 'pim-windows-payload-zip-'))
  const zipPath = join(zipDir, 'pim-windows.zip')
  try {
    const directoryManifest = readDirectoryManifest(root)
    writeZipFromDirectory(root, zipPath)
    const zipManifest = readZipManifest(zipPath)

    assert.deepEqual(directoryManifest.errors, [])
    assert.deepEqual(zipManifest.errors, [])
    assert.deepEqual(compareDirectoryAndZip(directoryManifest, zipManifest), [])

    writeFileSync(join(root, 'late-file.txt'), 'not in zip')
    const changedDirectory = readDirectoryManifest(root)
    assert.ok(
      compareDirectoryAndZip(changedDirectory, zipManifest)
        .some((error) => error.includes('late-file.txt')),
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zipDir, { recursive: true, force: true })
  }
})

test('CLI verifies the staged payload and ZIP used by build-windows.yml', () => {
  const root = makePayload()
  const zipDir = mkdtempSync(join(tmpdir(), 'pim-windows-payload-zip-'))
  const zipPath = join(zipDir, 'pim-windows.zip')
  const verifier = fileURLToPath(new URL('./verify-windows-payload.mjs', import.meta.url))
  try {
    writeZipFromDirectory(root, zipPath)
    execFileSync(process.execPath, [verifier, '--dir', root, '--zip', zipPath], {
      stdio: 'pipe',
    })

    writeFileSync(join(root, 'Microsoft.Web.WebView2.Core.dll'), 'retired')
    assert.throws(
      () => execFileSync(process.execPath, [verifier, '--dir', root, '--zip', zipPath], {
        stdio: 'pipe',
      }),
      (error) => error.status === 1,
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
    rmSync(zipDir, { recursive: true, force: true })
  }
})

test('unified Windows workflow starts from an empty daemon-only publish directory', () => {
  const workflow = readFileSync(
    fileURLToPath(new URL('../../.github/workflows/build-windows.yml', import.meta.url)),
    'utf8',
  )

  assert.doesNotMatch(workflow, /name:\s*Publish Shell\b/)
  assert.doesNotMatch(workflow, /client-shell-windows/)
  assert.doesNotMatch(workflow, /Pim\.Shell\.App/)
  const resetIndex = workflow.indexOf('Remove-Item -LiteralPath publish -Recurse -Force')
  const publishIndex = workflow.indexOf('dotnet publish Pim.Client.App/Pim.Client.App.csproj')
  assert.ok(resetIndex >= 0, 'publish directory reset is missing')
  assert.ok(publishIndex > resetIndex, 'daemon publish must start after the publish directory reset')
  assert.match(workflow, /verify-windows-payload\.mjs/)
})

test('installer excludes and deletes retired shell and WebView2 payloads', () => {
  const installer = readFileSync(
    fileURLToPath(new URL('../../installer/pim-setup.iss', import.meta.url)),
    'utf8',
  )

  assert.match(
    installer,
    /Excludes: "Pim\.Shell\.App\.\*,Microsoft\.Web\.WebView2\.\*,WebView2Loader\.dll"/,
  )
  assert.match(installer, /Type: files; Name: "\{app\}\\Pim\.Shell\.App\.\*"/)
  assert.match(installer, /Type: files; Name: "\{app\}\\Microsoft\.Web\.WebView2\.\*"/)
  assert.match(installer, /Type: files; Name: "\{app\}\\WebView2Loader\.dll"/)
  assert.match(
    installer,
    /Type: files; Name: "\{app\}\\runtimes\\win-x64\\native\\WebView2Loader\.dll"/,
  )
  assert.match(
    installer,
    /Type: files; Name: "\{app\}\\runtimes\\win-x86\\native\\WebView2Loader\.dll"/,
  )
  assert.match(
    installer,
    /Type: files; Name: "\{app\}\\runtimes\\win-arm64\\native\\WebView2Loader\.dll"/,
  )
})
