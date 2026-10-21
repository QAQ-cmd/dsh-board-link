# DSH Link plugin market index

This directory publishes the `dsh-board-link` plugin to the
[DSH plugin market](https://deepseek-harness.github.io/deepseek-harness/guide/plugin-market).

## Publish

Commit this `market/` directory to the default branch (`master`) of
`https://github.com/QAQ-cmd/dsh-board-link`.

The index URL is then:

```text
https://raw.githubusercontent.com/QAQ-cmd/dsh-board-link/master/market/index.json
```

If raw.githubusercontent.com is unavailable, the same index can be served by
jsDelivr:

```text
https://cdn.jsdelivr.net/gh/QAQ-cmd/dsh-board-link@master/market/index.json
```

In DSH: **插件 → 实验性插件 → 自定义插件市场 → 发现 → 添加索引源**, paste the URL.

## Contents

- `index.json` — plugin metadata and the pinned Git commit.
- `icons/dsh-board-link.png` — 256×256 marketplace icon.

The current version points at commit
`b0a2a585104eef5c8bc130bfafa69d497e3a0c5b`, plugin directory
`plugin/dsh-board-link`, package version `0.1.0`.

## Publishing a new version

1. Bump `plugin/dsh-board-link/package.json` and commit it.
2. Add an entry to `plugins[0].versions` with the new SemVer, the new commit
   SHA, and its Unix `publishTimestamp`.
3. Keep `repository.directory` pointing at `plugin/dsh-board-link`.
4. Commit and push `market/index.json`.
