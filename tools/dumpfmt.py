"""[Godhome] dump_fsm.py 导出文本的格式约定（导出和几个读它的工具共用，Codex 审计 0.10.3 PT04）。

- 字符串一律写成单引号，里面的 \\ ' 换行 制表符转义（和 Python 单引号字面量一样）。
  以前用 repr：字符串里有单引号时 repr 改用双引号，读的工具认不出，FSM 被当成不存在。
  读的时候两种引号都认（旧导出里有双引号的），用 ast.literal_eval 解。
- 动作行：`[(off) ]类型名[ «自定义显示名»](参数…)`。类型名和显示名分开取，显示名里有括号也不影响。
- FSM 标题行读不懂时直接报错（带文件和行号），不当成没有这个 FSM。
"""
import ast
import re

# 单引号或双引号字符串（带转义）。
QSTR = r"""('(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*")"""

FSM_HEAD_RE = re.compile(r"^=== FSM (.*?) :: " + QSTR + r"\s+start=" + r"""('(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"|None)""" + r"(.*)$")
ACTION_RE = re.compile(r"^(?:\(off\) )?([^\s(«]+)(?: «(.*?)»)?\(")


def q(s):
    """导出用：字符串写成单引号字面量（不转义中文，读起来和以前一样）。"""
    if s is None:
        return "None"
    s = str(s)
    return "'" + s.replace("\\", "\\\\").replace("'", "\\'").replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t") + "'"


def unq(tok):
    """读取用：单引号或双引号字面量解回字符串；None 还是 None。"""
    if tok == "None":
        return None
    v = ast.literal_eval(tok)
    if not isinstance(v, str):
        raise ValueError(f"不是字符串 / not a string: {tok!r}")
    return v


def parse_fsm_head(line, where=""):
    """FSM 标题行 → (物体路径, FSM 名, 起始状态, 后面的标记)。不是标题行返回 None；是标题行但读不懂就报错。"""
    if not line.startswith("=== FSM"):
        return None
    m = FSM_HEAD_RE.match(line)
    if not m:
        raise ValueError(f"[Godhome] 看不懂的 FSM 标题 / unparsable FSM header {where}: {line[:200]}")
    return m.group(1), unq(m.group(2)), unq(m.group(3)) or "", m.group(4)


def action_name(act):
    """动作行的类型名（去掉 (off) 和 «自定义显示名»）；不像动作行就返回整行第一个词。"""
    m = ACTION_RE.match(act)
    if m:
        return m.group(1)
    return act.replace("(off) ", "", 1).split("(", 1)[0].strip()
