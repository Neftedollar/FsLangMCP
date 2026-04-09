# Multi-Agent Workspace — zsh completions for multiagent-setup
# Source this file: source ./tools/completions.zsh
# Or add to ~/.zshrc: source /path/to/project/tools/completions.zsh

# --- /slash-commands via claude / nessy / gemini ---
# Completes role names from ~/.claude/commands/ and .claude/commands/
_ai_slash_commands() {
  local -a roles
  if [ -d "$HOME/.claude/commands" ]; then
    for f in "$HOME/.claude/commands"/*.md(N); do
      roles+=("/${${f:t}%.md}")
    done
  fi
  if [ -d ".claude/commands" ]; then
    for f in .claude/commands/*.md(N); do
      roles+=("/${${f:t}%.md}")
    done
  fi
  roles=(${(u)roles})
  _describe 'slash-command' roles
}

for _agent_cmd in claude nessy gemini; do
  if (( $+commands[$_agent_cmd] )); then
    compdef _ai_slash_commands $_agent_cmd
  fi
done
unset _agent_cmd

# --- multiagent-setup CLI ---
_multiagent_setup() {
  local -a subcommands providers hooks
  subcommands=(
    'new:Create a new multi-agent workspace'
    'add-provider:Add a provider to an existing workspace'
    'update:Update workspace templates to latest version'
    'sync-roles:Sync agent roles to ~/.claude/commands/'
    'install-mcps:Install age-mcp and o-brien MCP servers'
    'hook:Run a built-in hook (cross-platform)'
    'doctor:Check workspace health — tools, files, hooks, roles'
  )
  providers=(
    'claude:Claude Code by Anthropic (default)'
    'nessy:Nessy CLI (Claude-compatible alias)'
    'gemini:Google Gemini CLI'
    'codex:OpenAI Codex CLI'
    'qwen:Qwen Code by Alibaba'
    'cursor:Cursor IDE'
    'windsurf:Windsurf IDE by Codeium'
    'copilot:GitHub Copilot in VS Code'
    'cline:Cline extension for VS Code'
    'aider:Aider AI pair programmer'
    'continue:Continue.dev VS Code/JetBrains extension'
    'roo:Roo Code VS Code extension'
    'all:All providers at once'
  )
  hooks=(
    'block-dangerous' 'enforce-commit-msg' 'auto-lint'
    'log-agent' 'stop-guard' 'research-reminder'
  )

  case $CURRENT in
    2) _describe 'subcommand' subcommands ;;
    *) case ${words[2]} in
      new)
        case ${words[CURRENT-1]} in
          --provider) _describe 'provider' providers ;;
          *) _arguments '--provider[Provider to scaffold]:provider:->p' && _describe 'provider' providers ;;
        esac ;;
      add-provider)
        case $CURRENT in
          3) _describe 'provider' providers ;;
          *) compadd -- --force ;;
        esac ;;
      update)
        compadd -- --force ;;
      sync-roles)
        compadd -- --clone --pull --agency-dir ;;
      install-mcps)
        compadd -- --docker --manual --age-conn --obrien-conn --target ;;
      hook)
        _describe 'hook' hooks ;;
      doctor) ;; # no args
    esac ;;
  esac
}
compdef _multiagent_setup multiagent-setup
