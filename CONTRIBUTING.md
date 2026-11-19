# Como contribuir

Obrigado pelo interesse no Claude Color! Correções, novos dispositivos RGB, traduções e melhorias na documentação são bem-vindos.

## Antes de começar

- Para mudanças grandes (novo controlador, mudança de comportamento), abra uma **issue** primeiro para combinarmos a abordagem.
- Bugs pequenos e documentação podem ir direto para um PR.

## Fluxo

1. Faça um **fork** do repositório e clone o seu fork.
2. Crie uma branch a partir da `main`: `git checkout -b fix/descricao-curta`.
3. Faça as mudanças, compile e rode os testes:
   ```powershell
   dotnet build
   dotnet test
   ```
   Precisa do [.NET 10 SDK](https://dotnet.microsoft.com/download).
4. Faça commit com mensagens claras e envie para o seu fork.
5. Abra um **Pull Request** para a `main` deste repositório e preencha o modelo.

O CI (build e testes no Windows) precisa passar, e todo PR é revisado antes do merge. Ninguém faz push direto na `main`.

## Regras

- **Nunca** inclua o seu `config.json`, local key, Device ID, Access ID/Secret da Tuya ou qualquer dado pessoal. A configuração fica em `%USERPROFILE%\.claude-color`, fora do repositório.
- Nos testes, use valores falsos (ex.: `0123456789abcdef`, `127.0.0.1`).
- Siga o estilo do código existente (`.editorconfig`).
- Se a mudança aparece na interface, atualize o README e, se possível, as capturas em `docs/`.

## Falhas de segurança

Não abra issue pública: veja [SECURITY.md](SECURITY.md).
