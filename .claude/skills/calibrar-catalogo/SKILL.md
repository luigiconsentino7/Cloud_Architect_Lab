---
name: calibrar-catalogo
description: Calibra os valores do catálogo de serviços de nuvem (limites, latências, cotas, autoscaling e preços) em content/catalog/*.yaml a partir da documentação oficial dos provedores, registrando a fonte e a data da revisão. Use quando pedirem para revisar, corrigir ou atualizar números de um serviço (ex.: "calibre o SQS FIFO", "o limite do API Gateway mudou"), ou para adicionar um serviço novo ao catálogo.
---

# Calibrar o catálogo

> Comandos do CLI, na raiz do repositório: `./lab.sh <args>` (Linux, macOS, Git Bash), `.\lab.cmd <args>` (PowerShell) ou `lab <args>` (Prompt de Comando).

O simulador só é tão bom quanto o catálogo. Cada serviço (`content/catalog/aws.yaml`, `azure.yaml`, `gcp.yaml`)
sobrescreve os padrões do seu arquétipo (`content/catalog/archetypes.yaml`): desempenho (latência p50/p99, vazão
máxima, burst, concorrência, tempo de provisionamento), cotas, modos de falha e preços de referência na região de
São Paulo.

## Como calibrar

1. **Só fontes oficiais.** Documentação e páginas de preço do próprio provedor (`docs.aws.amazon.com`,
   `aws.amazon.com`, `learn.microsoft.com`, `cloud.google.com`) ou do projeto (`redis.io`, `kafka.apache.org`,
   `www.postgresql.org`). A lista aceita está em `src/Lab.Cli/ContentCommands.cs` (`OfficialDomains`). Blog de terceiros
   e respostas de fórum não servem de fonte.
2. **Leia a página antes de mudar o número**, e anote o que ela diz de fato: cota padrão ou máxima, por conta ou por
   recurso, por região, com ou sem pedido de aumento. Quando a documentação não publica um valor (ex.: latência
   típica), use um valor conservador, diga isso num comentário YAML e prefira a ordem de grandeza documentada.
3. **Registre a fonte e a data** no componente: `docs:` com a página principal ou `source: { url, reviewedAt }` quando o
   número vier de outra página. `reviewedAt` em `AAAA-MM-DD`, com a data em que você leu a fonte.
4. **Preços:** use a região sa-east-1 / Brazil South / southamerica-east1 e a unidade que o motor espera
   (veja os vizinhos no mesmo arquivo). Não misture preço por hora e por mês.
5. **Um componente padrão por arquétipo e provedor** (`isDefault: true`).

## Validar o impacto

```bash
./lab.sh catalog validate                      # fonte oficial e data em todo componente; um padrão por arquétipo
./lab.sh challenge validate --quick            # as referências continuam ≥ 85 com os números novos?
./lab.sh challenge validate                    # completo, antes de o usuário fazer o commit
```

Se uma referência cair abaixo de 85 por causa de um valor calibrado, o número novo é o certo: ajuste a semente
(capacidade, partições, orçamento) com a skill `novo-desafio`, não o catálogo. Mostre ao usuário a tabela antes/depois
dos valores alterados, com o link de cada fonte.
