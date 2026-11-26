# pauto

App nativo ultraleve para Windows que pausa o **Spotifast** automaticamente quando um player de vídeo/mídia começa a tocar em qualquer navegador e retoma a música sozinho quando o navegador para — **apenas** quando foi o `pauto` que pausou.

## Como funciona

- Usa o **SMTC (System Media Transport Controls)** nativo do Windows — sem extensão no navegador, sem API/login do Spotify e sem dependências externas.
- Reage por eventos do sistema (~0% de CPU parado).
- **Pausa instantânea (`0ms`)** quando qualquer navegador (Chrome, Edge, Brave, Opera/GX, Firefox, Zen, Arc, Vivaldi, etc.) começa a tocar mídia.
- **Retomada automática (`350ms` anti-flicker)** quando todos os navegadores param/pausam — evita o Spotifast piscar som quando você clica na barra de tempo do YouTube.
- Respeita seus comandos manuais:
  - Se você já tinha pausado o Spotifast manualmente, o `pauto` nunca dá play sozinho depois.
  - Se você der play manualmente no Spotifast enquanto um vídeo está rodando, o `pauto` não briga com você.

## Requisitos

- Windows 10 ou Windows 11 (usa apenas o compilador `csc.exe` e as bibliotecas `.NET Framework 4.x` / `WinMetadata` que já vêm no Windows).

## Compilar e testar

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Isso roda os testes unitários da máquina de estados e gera o executável em `dist\pauto.exe`.

Para rodar somente os testes:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -TestOnly
```

## Usar

Execute `.\dist\pauto.exe`. Um ícone discreto aparece na bandeja do sistema (perto do relógio):

- **Verde:** monitorando.
- **Âmbar (`||`):** Spotifast pausado pelo navegador.
- **Cinza:** monitoramento desativado.

Clique com o botão direito no ícone para:
- **Ativo** — ligar/desligar rapidamente (duplo clique no ícone também alterna).
- **Iniciar com o Windows** — ativa/desativa a inicialização automática em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Ver sessões de mídia** — lista os IDs exatos que o Windows está detectando no SMTC (útil para diagnóstico).
- **Sair** — encerra o app.
