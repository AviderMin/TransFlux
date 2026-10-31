# M2 · 可靠批量与持久化证据

日期：2026-10-31（Asia/Shanghai）。状态：**未执行（Not executed）**。

> 本文件是证据模板。M2 相关服务已由各 owner 编写（SqliteJobStore、OutputCommitter、OutputPathService、JobScheduler、RecoveryService 等），但 QA 尚未拿到可运行的集成宿主与故障注入脚本，因此以下用例均**未执行**，不得据此宣称通过。

## 环境（待补）

| 项 | 值 |
|---|---|
| OS | 待填 |
| .NET SDK | 10.0.401（预期） |
| Git commit | 待填 |
| 数据库路径 | 用户本地 `TransFlux/state.db`（测试用临时目录） |

## 待验证范围（docs/05、07、09）

| 用例 | 内容 | 状态 |
|---|---|---|
| T01 | 导入 1000 项并取消扫描；无重复枚举、取消后不再追加 | 未执行 |
| T03 | 多选混合设置冻结；排队 request 与实际参数一致 | 未执行 |
| T07 | 排序/暂停队列/取消；在途继续，未启动不启动，无漏释放令牌 | 未执行 |
| T10 | 两实例/两任务/外部程序抢占同名；无覆盖、无源修改 | 未执行 |
| T11 | Running/Verifying/Prepared/rename 后/DB 完成前分别强杀 | 未执行 |
| T12 | 重启恢复：活动任务 Interrupted；journal 对账；不冒领未知文件 | 未执行 |
| T13 | 磁盘满、权限变更、文件占用、UNC 断开；不假成功、源不变 | 未执行 |
| T14 | 取消与完成同刻、重复取消、过期 progress；终态唯一不倒退 | 未执行 |
| T15 | 清理历史/缓存、脱敏导出、preset 未知 schema；不误删输出 | 未执行 |
| T19 | 无时长/超短片；进度非 NaN/溢出；成功仅在验证提交后 | 未执行 |

## 关键不变量（执行时必须断言）

1. 每个 job 至多一个活跃 attempt；条件更新 `WHERE revision=expected` 生效。
2. 进度按 attemptId+sequence 单调；旧 attempt/乱序消息被丢弃。
3. `Succeeded` 仅在输出提交成功且 OutputRecord 落库后发出。
4. 提交日志在文件 rename 前写入；恢复按 journal 对账，不产生重复成品。
5. 崩溃/失败不把残缺输出当成功；源文件不变。

## 故障注入点（待脚本化）

- Running 中强杀 → 恢复后 Interrupted；
- Verifying 中强杀 → 恢复；
- Prepared 后、rename 前强杀 → 恢复；
- rename 后、DB 完成前强杀 → 恢复对账。

## 运行命令（待可执行后补充真实输出）

~~~
dotnet build TransFlux.slnx -c Debug
dotnet test tests/TransFlux.Infrastructure.Tests/TransFlux.Infrastructure.Tests.csproj -c Debug
# + 故障注入 harness（待实现）
~~~

## 未验证

- 全部 M2 用例未执行；SQLite 迁移/WAL/备份恢复、提交 journal、重启恢复均未实测。
