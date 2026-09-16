# Passagem de ponta a ponta — onde o jogador trava

Levantado lendo o código e inspecionando a cena `TransplanteCardiaco` reconstruída em
13/09/2026, etapa por etapa, na ordem em que o visitante as encontra.

**Não corrigi nada desta lista** — a tarefa pedia para levantar e reportar. Cada item diz o que
acontece, por que, e o que custaria resolver.

---

## Etapa 1 — Abrir o tórax

| | |
|---|---|
| O que o monitor diz | "Abra o tórax com a serra esternal" |
| O que o jogo espera | Manter a mão a até 8,5 cm da linha média do esterno por 3 s |

### T1. A instrução pede um instrumento que não existe — TRAVA

O texto manda usar a serra esternal. **Não há serra na cena**, nem bandeja, nem instrumento
algum: a esternotomia é um gesto de mão livre. O visitante vai procurar uma ferramenta,
não encontrar, e não ter motivo para encostar a mão no peito.

É a primeira coisa que ele faz no jogo. Se travar aqui, trava em tudo.

*Custo:* trocar a frase em `TransplantProcedure.CurrentInstruction` para descrever o gesto
("Pressione a mão sobre o esterno") — 2 min. Ou modelar a serra — decisão de arte.

### T2. Nenhum retorno de progresso durante os 3 s — TRAVA PARCIAL

`SternotomyWorker.Progress01` existe e é calculado, mas **nada o desenha**. O visitante segura
a mão no lugar certo e a tela não muda por três segundos. Três segundos sem retorno, para um
leigo de óculos pela primeira vez, é tempo suficiente para concluir que não funcionou e tirar a
mão — o que zera o progresso e confirma a conclusão errada.

Vale para **todos** os gestos por tempo do jogo: esternotomia (3 s), os 4 sítios de CEC
(26 s somados no nível Médio) e as 5 anastomoses (1,4 s cada).

*Custo:* uma barra ou anel de progresso no HUD lendo `Progress01` — ~30 min para os três casos.

---

## Etapa 2 — Entrar em circulação extracorpórea

| | |
|---|---|
| O que o monitor diz | "Canule as cavas e a aorta" |
| O que o jogo espera | Mão a até 2,8 cm de cada sítio de CEC, por 6,5 s cada |

### T3. Os 4 sítios de CEC são invisíveis — TRAVA

Mesmo defeito que você mandou corrigir no esterno, e que eu corrigi lá: em
`BuildBypassSites` cada sítio é criado como `new GameObject("Bypass_...")` **sem renderer
nenhum**. São transforms puros.

O raio é de 2,8 cm. Sem marcador, encontrar quatro pontos de 2,8 cm num tórax às cegas não é
uma tarefa difícil — é sorte.

As anastomoses já têm anéis, o esterno agora tem, os sítios de CEC não têm.

*Custo:* ~15 min. A função `BuildSternalMarker` que escrevi hoje serve com um parâmetro de cor;
é chamar uma vez por sítio.

---

## Etapa 3 — Retirar o coração doente

| | |
|---|---|
| O que o jogo espera | Agarrar o coração, levá-lo a 20 cm do assento, soltar |

### T4. O coração fica invisível sob a pele fechada — TRAVA

O `SternotomyController` abre o esterno e **ativa** o coração (`revealOnOpen`). Mas a pele do
paciente é uma malha fechada e opaca que **não abre** — é exatamente a malha de tórax aberto
que não existe no projeto.

Medido: a superfície da pele sobre o esterno está em Y=1,233; o coração está em Y≈1,12.
O coração é ativado dentro de um saco de pele intacto e o jogador não vê nada mudar.

A pele **não tem collider** (verificado), então a mão atravessa e o agarre funciona. Ou seja:
mecanicamente passa, visualmente é impossível saber que passou.

Isto é consequência direta do bloqueio da Fase 3 do relatório anterior. **Não tem conserto de
código** — precisa da malha.

*Custo:* a malha de tórax aberto. Decisão de arte, como registrado antes.

### T5. Os anéis das anastomoses também ficam sob a pele — TRAVA (etapa 5)

Mesmo motivo. Os anéis são transparentes (`_ZWrite 0`, fila 3000), mas ainda fazem teste de
profundidade contra a pele, que é opaca e desenhada antes. Ficam atrás dela, invisíveis.

O anel do esterno que adicionei hoje **não** tem esse problema: ele fica 3 mm **acima** da
superfície da pele, não dentro do corpo.

*Custo:* paliativo possível sem a malha — desenhar os anéis sempre por cima (`ZTest Always`).
Fica com aparência de raio-X, mas resolve a legibilidade. ~20 min. **Não implementei**: muda a
leitura visual do jogo e é decisão sua.

---

## Etapa 4 — Posicionar o coração do doador

### T6. O coração doador está a 98° do eixo do olhar — TRAVA

Já reportado e coberto por teste vermelho
(`TransplantErgonomicsTests.NothingEssential_SitsBeyondNinetyDegreesOfGaze`). O suporte fica
atrás da linha dos ombros; para achá-lo o visitante precisa girar o tronco.

*Custo:* uma linha em `BuildDonorHeart` — mas envolve decidir entre fidelidade (mesa lateral,
como numa sala real) e achabilidade.

---

## Etapa 5 — Conectar os vasos

Ver T5 (anéis sob a pele) e T2 (sem progresso). A mecânica em si está correta: raio de 2,2 cm,
1,4 s de contato, só na etapa certa, e agora funciona com a mão nua.

---

## Etapa 6 — Sair de bomba

Ver T3 (sítios invisíveis) e T2. As regras clínicas estão certas e testadas; o problema é
inteiramente de sinalização.

---

## Resumo

| # | Trava | Etapa | Natureza | Custo |
|---|---|---|---|---|
| T1 | Instrução pede serra que não existe | 1 | Texto | 2 min |
| T2 | Nenhum retorno de progresso nos gestos por tempo | todas | Código/HUD | ~30 min |
| T3 | 4 sítios de CEC sem marcador visível | 2 e 6 | Código | ~15 min |
| T4 | Pele fechada esconde o coração | 3 | **Precisa de asset** | arte |
| T5 | Anéis das anastomoses sob a pele | 5 | Asset, ou paliativo ~20 min | arte |
| T6 | Coração doador a 98° do olhar | 4 | Layout | 5 min + decisão |

**Quatro dos seis são de sinalização, não de lógica** — T1, T2, T3 e T6 somam cerca de uma hora
e não dependem de nenhum asset novo. T4 e T5 são a mesma falta de malha já registrada.

Nenhuma etapa deixa de **completar** por erro de lógica: com as mãos ligadas (tarefa 1 desta
sessão), toda a cadeia fecha. O que trava é o jogador não saber o que fazer, não o jogo recusar
o que ele fez.
