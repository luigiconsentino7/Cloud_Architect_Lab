# Cloud Architect Lab

Laboratório gamificado de **System Design** para engenheiros de software do mercado financeiro.

Você descreve o que quer praticar e escolhe a nuvem (AWS, Azure, GCP ou Genérico). O app propõe um problema realista, você desenha a solução num canvas estilo Excalidraw com componentes do provedor escolhido, e o app **testa o desenho como se estivesse em produção** — testes funcionais, carga, caos, integridade de dados, segurança e custo — antes de dar uma nota com pontos de melhoria.

- **Status:** especificação v0.1, ainda sem código.
- **Stack:** .NET 10 (LTS) no backend e Angular no frontend; as demais escolhas estão justificadas nos ADRs da especificação.
- **Custo recorrente:** zero. O app roda na sua máquina; IA em tempo de execução é opcional (modelo local gratuito, ou Claude API paga e desligada por padrão).

Especificação completa: [docs/spec/README.md](docs/spec/README.md)
