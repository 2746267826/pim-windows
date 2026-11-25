#!/usr/bin/env node
/**
 * Verify the daemon-only Windows payload before it is zipped or handed to Inno Setup.
 *
 * The unified installer used to publish Pim.Shell.App into the same directory as the
 * daemon. That made the retired shell, and its WebView2-only dependencies, part of both
 * pim-windows-v*.zip and PIM-Setup-*.exe even when the installer tried to exclude the
 * shell executable. This checker treats the staged directory and the produced ZIP as
 * two independently verified manifests.
 *
 * Usage:
 *   node scripts/ci/verify-windows-payload.mjs --dir src/client-windows/publish \
 *     --zip src/client-windows/pim-windows-vX.Y.Z.zip
 */
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { inspectZip, listFilesRecursive } from './zip-writer.mjs'

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

const RETIRED_PAYLOAD_PATTERNS = [
  {
    label: 'retired Pim.Shell.App payload',
    pattern: /(^|\/)[^/]*pim\.shell\.app[^/]*(\/|$)/i,
  },
  {
    label: 'WebView2 dependency',
    pattern: /(^|\/)[^/]*webview2[^/]*(\/|$)/i,
  },
]

export function normalizeEntryName(name) {
  return name.replaceAll('\\', '/').replace(/^\.\//, '')
}

/**
 * @param {string[]} entries
 * @returns {{ entries: string[], errors: string[] }}
 */
export function validateWindowsPayloadEntries(entries) {
  const normalized = entries.map(normalizeEntryName).filter(Boolean)
  const entrySet = new Set(normalized)
  const errors = []

  if (normalized.length === 0) {
    errors.push('payload is empty')
  }

  for (const required of REQUIRED_FILES) {
    if (!entrySet.has(required)) {
      errors.push(`missing required payload entry: ${required}`)
    }
  }

  for (const entry of normalized) {
    for (const retired of RETIRED_PAYLOAD_PATTERNS) {
      if (retired.pattern.test(entry)) {
        errors.push(`${retired.label} must not be shipped: ${entry}`)
      }
    }
  }

  return { entries: [...entrySet].sort(), errors }
}

export function readDirectoryManifest(publishDir) {
  return validateWindowsPayloadEntries(
    listFilesRecursive(publishDir).map((relativePath) => normalizeEntryName(relativePath)),
  )
}

export function readZipManifest(zipPath) {
  const { entryNames } = inspectZip(zipPath)
  return validateWindowsPayloadEntries(entryNames.filter((name) => !/[\\/]$/.test(name)))
}

/**
 * The ZIP must contain every staged file and nothing that was not staged. Directory
 * entries are ignored because archivers may emit them while the manifest only contains
 * files.
 */
export function compareDirectoryAndZip(directoryManifest, zipManifest) {
  const directoryFiles = new Set(directoryManifest.entries)
  const zipFiles = new Set(zipManifest.entries)
  const errors = []

  for (const entry of directoryFiles) {
    if (!zipFiles.has(entry)) {
      errors.push(`ZIP is missing staged file: ${entry}`)
    }
  }
  for (const entry of zipFiles) {
    if (!directoryFiles.has(entry)) {
      errors.push(`ZIP contains a file that was not staged: ${entry}`)
    }
  }

  return errors
}

function parseArgs(argv) {
  const result = { directory: '', zip: '' }
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index]
    if (argument === '--dir') {
      result.directory = argv[++index] ?? ''
    } else if (argument === '--zip') {
      result.zip = argv[++index] ?? ''
    } else {
      throw new Error(`unknown argument: ${argument}`)
    }
  }
  return result
}

function main() {
  const args = parseArgs(process.argv.slice(2))
  if (args.directory === '' && args.zip === '') {
    throw new Error('provide --dir and/or --zip')
  }

  const errors = []
  let directoryManifest = null
  let zipManifest = null

  if (args.directory !== '') {
    directoryManifest = readDirectoryManifest(args.directory)
    errors.push(...directoryManifest.errors.map((error) => `staging: ${error}`))
  }
  if (args.zip !== '') {
    zipManifest = readZipManifest(args.zip)
    errors.push(...zipManifest.errors.map((error) => `zip: ${error}`))
  }
  if (directoryManifest !== null && zipManifest !== null) {
    errors.push(...compareDirectoryAndZip(directoryManifest, zipManifest))
  }

  if (errors.length > 0) {
    console.error('Windows payload manifest verification failed:')
    for (const error of errors) {
      console.error(`  - ${error}`)
    }
    return 1
  }

  if (directoryManifest !== null) {
    console.log(`staging payload: ${directoryManifest.entries.length} files`)
  }
  if (zipManifest !== null) {
    console.log(`ZIP payload: ${zipManifest.entries.length} files`)
  }
  console.log('Windows payload manifest is daemon-only and contains no retired shell/WebView2 files.')
  return 0
}

const invokedDirectly = (() => {
  if (!process.argv[1]) return false
  try {
    return resolve(fileURLToPath(import.meta.url)) === resolve(process.argv[1])
  } catch {
    return false
  }
})()

if (invokedDirectly) {
  try {
    process.exitCode = main()
  } catch (error) {
    console.error(`verify-windows-payload: ${error.message}`)
    process.exitCode = 2
  }
}
