# recon/probes/ · 一次性探针归档（S0 · 2026-10-14）

这些脚本原散落在 `ControlCenter/` 根目录，是开发期针对真机的**一次性探针 / 验收工具**。
S0 版本控制止血时整体迁到此处，路径依赖已统一修好（`_s0_fix_paths.py` 记录改动）。

## 路径约定（历史）

脚本里会看到 `sys.path.insert(..., "..", "..", "ControlCenter")` —— 当年这些小工具靠它 import
散在 `ControlCenter/` 里的模块，**那些模块现在不存在了**。所以除了只依赖标准库 / `pywin32` / HTTP
的几个（`sniff.py`、`_probe_*.py` 这类），其余脚本只作记录：要复现就照它的手法重写一个。

历史坑：只改 `sys.path`、不 `chdir` —— 模块用自身 `__file__` 定位，chdir 会把它带歪。

## 分类索引

| 前缀 | 用途 |
|---|---|
| `_probe_hwinfo*.py` / `_probe_hwi_all.*` | HWiNFO 共享内存结构 / 全量传感器枚举 / 托盘图标归属 |
| `_probe_live.py` / `_probe_perf_lag.py` | 采样延迟实测（快照 1.5~2.8s、写后 6.28s 才反映的出处） |
| `_probe_namedobj.py` / `_probe_tray_dump.py` | 内核对象名枚举 / Win10 托盘工具栏（Win11 已无效，留作参考） |
| `_fan_auto*.py` / `_fan_*` | 风扇自动/手动/停转/释放行为标定（EC 拿到 UFA 就不接管的实证系列） |
| `_fn1_probe.py` / `_watch_fn.py` | FN+1 定位探针（深字段 RPM5/ENTP 采集，结论未定，见 PROTOCOL §十） |
| `_verify_gaps.py` / `_disk_load.py` | 协议 gap 核对 / 磁盘造负载 |
| `_probe_power_sources.py` | CPU 功耗两条来源（PDH RAPL ↔ HWiNFO）对照 + 采集阶段计时；**内含"PDH 是速率计数器，同一秒读两次必得垃圾"的实证** |
| `_probe_setactive.py` | "功耗突然下降"判别探针：单独重放 `powercfg /setactive`（不碰 EC）看能否复现 —— 2026-10-16 结论是**复现不出来** |

## 注意

- 大部分探针打 **HTTP 127.0.0.1:8765**（运行中的控制中心），跑之前先确认实例在跑；
- `dump-ec-admin.cmd` 引用的 `_probe_ec3.py` 已不存在（EC dump 早已采完），
  留档只为记录「dump 是怎么来的」；
- `.out` / `.log` 输出一律不在此目录留存（一次性产物，见 `.gitignore`）。
