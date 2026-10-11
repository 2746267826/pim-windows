#!/usr/bin/env node
/**
 * 审计 F-12 / AC-COM-05 的行为测试：PR 元数据校验必须对**真实形态的 payload**
 * 判定正确 —— 合规的通过，缺章节 / 非双语 / 分支名不对的失败。
 *
 * 这些 payload 直接对应 CI 传给脚本的
 * `github.event.pull_request.title` / `.body` / `github.head_ref`。
 *
 * 用法：node --test scripts/ci/test-pr-metadata.mjs
 */

import assert from 'node:assert/strict';
import test from 'node:test';

import { checkPrMetadata } from './check-pr-metadata.mjs';

const GOOD_BODY = [
  '## 概述 / Overview',
  '',
  '修一件事 / Fix one thing',
  '',
  '## 技术修改 / Technical changes',
  '',
  '- 改了什么',
  '',
  '## 功能变化 / Feature changes',
  '',
  '- 用户可见变化',
  '',
  '## 如何体验 / How to try it',
  '',
  '- 打开页面点一下',
  '',
  '## 测试 / Tests',
  '',
  '- `dotnet test` -> 通过',
].join('\n');

test('合规的 PR payload 通过', () => {
  const violations = checkPrMetadata({
    title: 'fix: 修正队列计数 / fix queue depth',
    body: GOOD_BODY,
    headRef: 'deepseek-linux/audit-remediation',
  });
  assert.deepEqual(violations, []);
});

test('工具自动创建的分支（task/<n>）被允许', () => {
  const violations = checkPrMetadata({
    title: 'chore: 维护 / maintenance',
    body: GOOD_BODY,
    headRef: 'task/42',
  });
  assert.deepEqual(violations, []);
});

test('只有英文标题（非双语）被拒绝', () => {
  const violations = checkPrMetadata({
    title: 'fix queue depth',
    body: GOOD_BODY,
    headRef: 'deepseek-linux/audit-remediation',
  });
  assert.equal(violations.length, 1);
  assert.match(violations[0], /双语/);
});

test('缺少「测试 / Tests」章节被拒绝', () => {
  const body = GOOD_BODY.replace('## 测试 / Tests', '## 其他 / Other');
  const violations = checkPrMetadata({
    title: 'fix: 修正 / fix',
    body,
    headRef: 'deepseek-linux/audit-remediation',
  });
  assert.equal(violations.length, 1);
  assert.match(violations[0], /测试/);
});

test('分支名不符合 {agent}-{os}/{topic} 被拒绝', () => {
  const violations = checkPrMetadata({
    title: 'fix: 修正 / fix',
    body: GOOD_BODY,
    headRef: 'feature/queue-depth',
  });
  assert.equal(violations.length, 1);
  assert.match(violations[0], /head 分支/);
});

test('os 段不是 win/linux 的分支被拒绝', () => {
  const violations = checkPrMetadata({
    title: 'fix: 修正 / fix',
    body: GOOD_BODY,
    headRef: 'deepseek-macos/audit',
  });
  assert.equal(violations.length, 1);
  assert.match(violations[0], /head 分支/);
});

test('完全空的 payload（push 事件没有 PR body）会逐项报缺陷，而不是静默通过', () => {
  const violations = checkPrMetadata({ title: '', body: '', headRef: '' });
  // 1 个标题 + 4 个章节 + 1 个分支
  assert.equal(violations.length, 6);
});
