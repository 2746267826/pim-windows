import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import vm from 'node:vm'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../..')
const harnessFile = path.join(repoRoot, 'tests/ClientWindows/HostSessionWindowsHarnessTests.cs')

function extractProbeScript() {
  const source = fs.readFileSync(harnessFile, 'utf8')
  const raw = source.match(/private const string ProbePage = """\n([\s\S]*?)\n""";/)
  if (!raw) throw new Error('ProbePage raw string not found in HostSessionWindowsHarnessTests.cs')

  const html = raw[1]
    .replaceAll('__EXTERNAL_ORIGIN__', 'http://127.0.0.1:45678')
    .replaceAll('__NONCE__', 'nonce_12chars')
    .replaceAll('__READY_REQUEST_ID__', 'req-ready-1')

  const script = html.match(/<script>\n([\s\S]*?)\n<\/script>/)?.[1]
  if (!script) throw new Error('ProbePage script element not found')
  return script
}

async function runProbe(mode = 'clean') {
  const script = extractProbeScript()
  let listener
  let report
  const storage = { length: 0 }
  const context = {
    console,
    document: { documentElement: { innerHTML: '<html>clean</html>' }, cookie: '' },
    sessionStorage: storage,
    localStorage: storage,
    setTimeout: (fn) => { fn(); return 1 },
    fetch: async (url, init = {}) => {
      if (String(url) === '/api/report') report = JSON.parse(init.body)
      const body = String(url) === '/today-marker' ? 'marker' : '{"status":"ok"}'
      return { ok: true, text: async () => body }
    },
  }
  context.window = context
  context.window.__PIM_HOST_CHANNEL__ = { v: 1, product: 'pim-host-windows' }
  if (mode === 'leak-window') context.window.__pimToken = 'ACCESS-CANARY-9f3a1c'
  context.window.chrome = {
    webview: {
      addEventListener: (_name, handler) => { listener = handler },
      postMessage: (text) => {
        const frame = JSON.parse(text)
        const state = {
          v: 1,
          type: 'host.state',
          requestId: frame.requestId,
          clientNonce: frame.clientNonce,
          handshakeId: '6f1e8c0a-1b2c-4d3e-8f90-123456789abc',
          product: 'pim-host-windows',
          apiOrigin: 'http://127.0.0.1:12345',
          authenticated: true,
        }
        if (mode === 'leak-frame') state.auditExtra = 'ACCESS-CANARY-9f3a1c'
        listener({ data: state })
      },
    },
  }

  vm.runInNewContext(script, context, { filename: 'ProbePage.js' })
  for (let i = 0; i < 100 && !report; i++) await new Promise((resolve) => setImmediate(resolve))
  if (!report) throw new Error('ProbePage did not post /api/report')
  return report
}

test('ProbePage JavaScript raw template parses without syntax error', () => {
  const script = extractProbeScript()
  assert.doesNotThrow(() => {
    new vm.Script(script, { filename: 'ProbePage.js' })
  })
})

test('ProbePage clean run does not detect canary (pageSawCanary === false)', async () => {
  const report = await runProbe('clean')
  assert.equal(report.pageSawCanary, false)
  assert.equal(report.apiFetchOk, true)
  assert.equal(report.pageFetchOk, true)
  assert.equal(report.externalFetchOk, true)
  assert.equal(report.apiBody, '{"status":"ok"}')
  assert.equal(report.pageBody, 'marker')
  assert.equal(report.externalBody, '{"status":"ok"}')
})

test('ProbePage window.__pimToken leak is detected (pageSawCanary === true)', async () => {
  const report = await runProbe('leak-window')
  assert.equal(report.pageSawCanary, true)
})

test('ProbePage bridge receivedFrames leak is detected (pageSawCanary === true)', async () => {
  const report = await runProbe('leak-frame')
  assert.equal(report.pageSawCanary, true)
})
