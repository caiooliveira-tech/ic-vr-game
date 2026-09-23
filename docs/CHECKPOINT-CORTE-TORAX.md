# Checkpoint de desenvolvimento — 23/09/2026

Backup solicitado antes de continuar o desenvolvimento. **Não é uma versão aprovada
da física da pele nem uma simulação médica validada.**

## Cena de trabalho

Abra `Assets/Scenes/ThoraxCuttingPlayable.unity` no Unity 6000.3.22f1.
A cena usa uma cópia da região torácica do modelo existente.

- Mouse esquerdo: trajetória do bisturi.
- Mouse direito: tentativa de segurar e afastar a pele após o corte.
- F durante o arraste: manter a posição; Espaço: soltar as retenções.
- R: reiniciar a pele; roda do mouse: aproximar/afastar a câmera.

Esses controles estão implementados, mas a abertura interativa ainda precisa passar
pela validação completa. Não apresentar este checkpoint como cirurgia finalizada.

## Estado preservado

- Recorte de triângulos com conectividade topológica explícita das bordas.
- Faixas internas com vértices compartilhados e correspondência com as bordas.
- Cena desktop e atualização do collider.
- Nova deformação por rede reduzida dependente da conectividade da superfície,
  retenção de pontos e retorno amortecido; ainda experimental.
- Alterações já existentes na cena `TransplanteCardiaco` também foram guardadas
  neste backup, sem uma nova edição dessa cena durante a publicação.

## Pendência que motivou a interrupção

Um teste de arraste de 10 mm produziu aproximadamente 6,7 mm em uma borda acessível,
mas o ponto central foi rejeitado porque bordas internas da malha de origem estavam
sendo tratadas como apoios fixos. A proteção geométrica também limitou o movimento.
A malha extraída possui 856 arestas de contorno topológico, incluindo regiões internas;
ela ainda não foi reparada. O afastamento dos dois lados e a recuperação completa
precisam ser validados novamente após a correção.

A captura `Captures/thorax-opening-development.png` registra o estado visual de
desenvolvimento, não um resultado aprovado de retração bilateral.

Não foram incluídos no backup: `Library`, caches, logs, a cópia externa
`VR-Surgery-Reference`, documentos de planejamento alheios a este incremento e a
ponte temporária local usada para recuperar a conexão com o Editor.
