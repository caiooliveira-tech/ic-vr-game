# Relatório — sessão noturna de 13/09/2026

Modo exploratório. Fases 1, 2 e 4 executadas; **Fase 3 não iniciada por decisão do portão da
Fase 1**, que é o resultado mais importante da noite.

**Suíte no início: 121/121.** **Suíte no fim: 123 testes, 122 passando, 1 falhando** — a que
falha é um teste que escrevi nesta sessão, e ela falha porque encontrou um defeito real
(ver 4.2).

---

## LEIA ISTO PRIMEIRO — três coisas que travam o projeto

### 1. Não existe malha de tórax aberto. A Fase 3 não pôde começar.

Nenhum dos modelos anatômicos tem bordas de pele afastadas, subcutâneo, músculo ou esterno
exposto. São todos casca única fechada, um objeto, um submesh. Detalhe em 1.1.

Conforme o portão que você definiu: pulei para a Fase 4 e não improvisei anatomia.

**Decisão sua:** a malha precisa ser produzida ou adquirida antes que o corte de pele exista.
É decisão de arte, não de código.

### 2. `XRHandInteractor` não está na cena. `IsHeld` é sempre falso.

Descoberto ao executar a tarefa 2.3. A cena `TransplanteCardiaco` tem 0 instâncias de
`XRHandInteractor` — o adaptador que traduz os eventos da XRI para o nosso
`SurgicalInteractable`. Consequências, ambas verificadas na cena aberta:

- **`GrabbableOrgan` do coração nativo nunca dispara.** Ele só avalia no evento `Released`,
  que nunca é levantado. A etapa `RemoveNativeHeart` é um beco sem saída no jogo real.
- **O coração doador "assenta" ainda na mão.** A avaliação exige `distance <= tolerância &&
  !IsHeld`; com `IsHeld` sempre falso, ele conta como implantado assim que passa a 3,5 cm,
  mesmo sendo segurado.

Não corrigi: a regra da sessão manda reportar em vez de implementar, e a correção envolve uma
escolha que é sua — **em qual interactor do rig o `XRHandInteractor` deve entrar**. O rig expõe,
por mão: `Near-Far Interactor`, `Poke Interactor`, `Teleport Interactor`, além de `Gaze` e
`Climb`. O candidato certo é provavelmente o `Near-Far Interactor` (é o que faz grab), mas
colocar no errado cria bug sutil em vez de resolver.

Custo estimado de corrigir: ~15 min no `TransplanteSceneBuilder`, depois de você escolher o
interactor.

### 3. Não existe repositório git. Nenhum commit foi feito.

`git rev-parse` falha aqui e em `/home/samuel/Downloads`. Existe `.gitignore` (26/08), e o nome
da pasta — `ic-vr-game-main` — é o padrão de ZIP extraído do GitHub, o que sugere que **o
repositório de verdade está em outro lugar** e esta cópia é um snapshot solto.

Não rodei `git init`: criaria um repositório divergente do seu, e isso é decisão de estrutura
de projeto. **Nenhuma fase foi commitada.** As mudanças estão no disco, listadas no fim.

---

## FASE 1 — DIAGNÓSTICO

### 1.1 Inventário de malhas anatômicas — PRONTO

Medido carregando cada asset e somando triângulos de todos os `MeshFilter`/`SkinnedMeshRenderer`.

| Asset | Objetos | Submeshes | Triângulos | Vértices |
|---|---:|---:|---:|---:|
| `PATIENT_Ribcage.glb` | 1 | 1 | 80.000 | 62.250 |
| `PATIENT_Heart.glb` | 1 | 1 | 60.000 | 50.894 |
| `PATIENT_BodySkin.glb` | 1 | 1 | 29.999 | 20.633 |
| `PATIENT_ExternalBody.fbx` | 1 | 1 | 28.960 | 16.397 |
| `PATIENT_Vertebrae.fbx` | 1 | 1 | 22.532 | 12.973 |
| `PATIENT_Sternum.glb` | 1 | 1 | 12.000 | 11.279 |
| `PROP_OperatingTable.glb` | 1 | 1 | 25.000 | 28.247 |
| `PROP_InstrumentTray.fbx` | 1 | 1 | 668 | 476 |
| `SCALPEL_Bisturi.fbx` | 3 (`filo`, `mango`, `tope`) | 3 | 6.963 | 4.711 |

**Existe malha de tórax aberto? NÃO.** Sendo literal, como você pediu:

- Não há segundo variante de nenhuma malha anatômica. Nenhum arquivo com `open`, `aberto`,
  `incis`, `cut`, `muscle`, `subcut`, `flap`, `wound` no nome em todo o `Assets/`. Os dois
  únicos resultados dessa busca são `Primitive_Torus_Cut.fbx` e `Torus-Cut.prefab`, que são
  demo da XR Interaction Toolkit, não anatomia.
- `PATIENT_BodySkin` é **um objeto, um submesh, um material**. Não há grupo separado de pele,
  gordura, músculo ou plano de fáscia. Não há bordas de incisão. Não há cavidade — a superfície
  do tronco é contínua onde o esterno estaria.
- `PATIENT_Sternum` é um osso isolado e fechado. Não traz tecido mole em volta nem faces
  internas de corte.
- Não existe geometria de "lábio" de ferida em lugar nenhum: nenhuma malha tem submesh
  secundário que pudesse servir de face interna revelada.

**Consequência:** qualquer técnica de revelação (dissolve, máscara, troca de malha) não teria o
que revelar. Abaixo da pele não há nada modelado.

### 1.2 Paciente em decúbito dorsal — PRONTO E CORRIGIDO

Você estava certo: **o paciente estava de bruços.**

Confirmei por medida antes de mexer, não de olho. O eixo frontal do modelo é o **local +Z**:

- Tornozelos (0–4% da estatura): os dedos alcançam 17,3 mm em +Z contra 13,0 mm dos calcanhares
  em −Z.
- Quadril (45–55%): os glúteos alcançam 17,3 mm em −Z contra 11,7 mm da barriga em +Z.

A rotação era `Euler(90, 0, 0)`, que manda o local +Z para o mundo **−Y** — ou seja, peito para
baixo. Corrigido para `Euler(-90, 180, 0)`: cabeça para +Z, peito para +Y.

É exatamente a mesma rotação que o `SupineRotationFor` do gradil costal já usava no outro ramo —
o código já sabia a resposta, o corpo é que usava a errada.

Verificado por imagem, de frente e de lado: rosto, peito, mamas e dedos dos pés para cima,
costas apoiadas no tampo em Y=0,950. A mesa foi conferida junto (rotação identidade, escala 1,
tampo em Y=0,950 coincidindo com o colisor).

Arquivo: `Assets/Editor/TransplanteSceneBuilder.cs`, método `BuildPatient`.

### 1.3 Defeitos de geometria da mesa — LISTADO, NÃO CORRIGIDO

Medido, não estimado. Três coisas que **não** são defeito, para você não perder tempo:

- Pivot na base (`mesh.bounds.center` = 0, 0.475, 0). É a convenção correta para prop de chão.
- Escala 1,1,1 na raiz e no modelo. Sem distorção.
- **Não atravessa o chão:** `min.y = 0,0000` exato. O topo visual e o topo do `BoxCollider`
  coincidem em Y=0,950.

O defeito real é um só, e é grave:

- **A malha é uma sopa de cascas.** 26.664 arestas de borda em 50.832 arestas totais — **52,5%
  das arestas são abertas**. A mesa não é um sólido; é um monte de retalhos desconectados.
  É o mesmo problema que o README já documenta para o coração. É isso que produz as fendas e os
  estilhaços pretos e brancos visíveis no colchão quando se olha de perto.
- Normais: apenas 0,9% (223 de 25.000 faces) têm o winding discordando da normal gravada. **Não
  é inversão sistemática** — descarte essa hipótese.
- Triângulos duplicados/coincidentes: **zero**. Descarte z-fighting por faces sobrepostas.

Corrigir é retopologia/fechamento no modelo — decisão de arte, como você definiu. Não toquei.

Fora da mesa, dois placeholders na mesma cena que valem menção: a coluna do suporte do doador é
um `Cube` primitivo e a bacia é um `Cylinder` primitivo, ambos gerados por código.

### 1.4 Performance atual — MEDIDO

Duas medições, porque medem coisas diferentes.

**Estático (conteúdo da cena, sem render):**

| Métrica | Valor |
|---|---:|
| Renderers ativos | 53 (2 skinned) |
| Triângulos | 296.408 |
| Vértices | 243.612 |
| Malhas únicas | 36 |
| **Materiais únicos** | **29** ← piso de draw calls por passe |
| Marcados *static* | 7 renderers / 4.384 tris |
| **Dinâmicos** | **46 renderers / 292.024 tris (98,5%)** |
| Lançam sombra | 45 |
| Luzes | 1 direcional, modo *Mixed*, sombras *Soft* |

**Em play mode, `UnityStats` na Game view (815×420, visão única, não estéreo):**

| Métrica | Valor |
|---|---:|
| **Draw calls** | **107** |
| Batches | 99 |
| SetPass calls | 39 |
| Triângulos | 338.948 |
| Vértices | 279.912 |
| Static batched draw calls | 2 |
| Dynamic batched draw calls | 9 |
| Shadow casters | 23 |

Objetos acima de 3.000 triângulos, todos **dinâmicos** (nenhum se beneficia de static batching):
`RibcageModel` 80.000 · `HeartModel` 60.000 · `DonorHeartModel` 60.000 · `Body_Skin` 29.999 ·
`TableModel` 25.000 · `SternumModel` 12.000.

Não otimizei nada, conforme instruído.

**Ressalva honesta:** isto é o Editor em Linux, visão única, numa janela de 815×420. **Não é
medição no headset.** Em estéreo o número de draw calls tende a subir substancialmente se o
projeto não estiver em single-pass instanced — e eu **não verifiquei** qual modo de stereo
rendering está configurado.

### 1.5 Suíte completa — 121/121

Número real, rodado. Zero falhas, zero puladas, 40,2 s.

---

## FASE 2 — PESQUISA

### 2.1 Orçamento da Meta para Quest standalone vs. o nosso — PRONTO

Fonte: [Testing and performance analysis — Meta Horizon OS Developers](https://developers.meta.com/horizon/documentation/unity/unity-perf/), acessada em 13/09/2026.

| | Meta recomenda | Nós | Situação |
|---|---|---:|---|
| Frame rate (app interativo) | **mínimo 72 FPS** | — | 72 Hz = 13,9 ms/frame |
| Draw calls, Quest 2/Pro | "Busy" 80–200 · "Medium" 200–300 · "Light" 400–600 | **107** | **dentro**, faixa "busy" |
| Draw calls, Quest 3/3S | "Busy" 200–300 · "Medium" 400–600 · "Light" 700–1000 | **107** | **folgado** |
| Triângulos, Quest 2/Pro | **750k–1M** | **338.948** | **~45% do teto** |
| Triângulos, Quest 3/3S | **1,3M–1,8M** | **338.948** | **~26% do teto** |

**Conclusão com números: triângulos não são o nosso problema.** Estamos a menos da metade do
teto mais conservador da Meta, mesmo com a anatomia inteira em malha cheia. Os draw calls (107)
estão dentro da faixa apertada do Quest 2 e confortáveis no Quest 3.

O ponto de atenção não é nenhum dos dois tetos: é que **98,5% da geometria é dinâmica** e
**45 objetos lançam sombra** com uma direcional de sombra *Soft*. Sombra é um passe extra sobre
os mesmos objetos.

**Isto contradiz o README.** Ver a seção de contradições.

### 2.2 Conforto em VR de pé — PARCIALMENTE VERIFICADO, NÚMEROS NÃO ENCONTRADOS

Fonte: [Locomotion comfort and usability](https://developers.meta.com/horizon/design/locomotion-comfort-usability/) e [Head — Meta Horizon OS Developers](https://developers.meta.com/horizon/design/head/), acessadas em 13/09/2026.

O que a Meta afirma, qualitativamente:

- Manter o usuário em posição neutra de corpo o máximo possível; mãos quase sempre à frente do
  headset, o que também é a posição ideal para o rastreamento.
- Interactables devem ficar a uma altura confortável para evitar que o usuário olhe para baixo
  constantemente, reduzindo tensão cervical.
- Único número concreto encontrado, e é de espaço de jogo, não de alcance: modo estacionário
  "aproximadamente 1 metro por 1 metro".

**O que eu NÃO consegui verificar (3 tentativas, 2 páginas retornaram 404):** a Meta não publica,
nas páginas que consegui alcançar, valores numéricos de alcance confortável em metros, de altura
recomendada de superfície de trabalho, nem de limite angular fora do eixo do olhar.

**Achado relevante que decorre disso:** as constantes de ergonomia que este projeto *impõe como
teste* não têm fonte citável por trás. Em `Assets/Scripts/Diagnostics/ReachEnvelope.cs`:
`ShoulderHalfWidth = 0,19 m`, `PrecisionReach = 0,55 m`, `ComfortableReach = 0,70 m`,
`MaximumReach = 0,82 m`, e o limite de 90° do eixo do olhar. São estimativas internas plausíveis,
não números da Meta. Estão marcados aqui como **não verificados**.

Avaliação do nosso layout contra o que dá para afirmar: o campo operatório fica a 22–33° abaixo
do eixo do olhar e a 1–9° de desvio lateral, com todos os pontos entre 0,45 m e 0,57 m dos
ombros. Isso é compatível com "mãos à frente, sem olhar muito para baixo". A exceção está em 4.2.

### 2.3 XR Interaction Toolkit 3.5.1 — PRONTO, E REVELOU O DEFEITO Nº 2

Fonte: [XRBaseInteractable — XR Interaction Toolkit 3.5](https://docs.unity3d.com/Packages/com.unity.xr.interaction.toolkit@3.5/api/UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable.html), acessada em 13/09/2026.

A API documentada para detectar instrumento segurado, na nossa versão exata:

- `isSelected` (`bool`, somente leitura) — "Indicates whether this interactable is currently
  being selected by any interactor."
- `interactorsSelecting` (`List<IXRSelectInteractor>`) — lista dos interactors selecionando.
- `firstInteractorSelecting` (`IXRSelectInteractor`).
- Eventos `selectEntered` / `selectExited` (`SelectEnterEvent` / `SelectExitEvent`), mais
  `firstSelectEntered` e `lastSelectExited`.

**Nossa implementação segue a API, não a contorna.** `XRHandInteractor` assina
`selectEntered`/`selectExited` do interactor e repassa para `SurgicalInteractable.OnGrabbed/
OnReleased`; `ToolReleasePhysics` e `ForcepsGripInput` também usam os eventos oficiais. É um
adaptador legítimo em cima da API pública — sobrevive a atualização.

**Porém:** esse adaptador **não está na cena do transplante**. Ver o item 2 do topo. É o achado
mais sério da noite depois do portão.

Observação adicional: como temos `isSelected` disponível nativamente, o campo próprio `IsHeld` do
`SurgicalInteractable` é redundante e é justamente ele que está quebrado por falta do adaptador.
Uma alternativa à correção proposta seria ler `XRGrabInteractable.isSelected` direto. **Não
implementei nenhuma das duas** — é decisão sua.

### 2.4 Revelação progressiva de malha em mobile VR — PESQUISADO, SEM CUSTO EM MS VERIFICÁVEL

Alimentaria a Fase 3, que está bloqueada. Registrado para quando a malha existir.

Fontes: [Draw Call Cost Analysis for Meta Quest](https://developers.meta.com/horizon/documentation/unity/po-draw-call-analysis/) e [Transparency Best Practices for URP on Oculus Quest — fórum Meta](https://communityforums.atmeta.com/discussions/dev-unity/transparency-best-practices-for-universal-render-pipeline-on-oculus-quest/808015), acessadas em 13/09/2026.

- A recomendação de melhores práticas para mobile é **evitar transparência por alpha-test /
  pixel discard**, porque o custo é alto em GPU de tile. Isso é relevante porque **dissolve
  mascarado é exatamente alpha-test**. *(marcado como fórum Meta, não documentação — a doc
  oficial que encontrei só diz "alpha blending is not cheap")*
- A página oficial de custo de draw call dá **apenas custos relativos** (troca de material: +64%
  no tempo de draw call; troca de shader: +175%; redesenhar o mesmo objeto: ~25% do custo de
  desenhar outro) e declara que os testes foram feitos **em Meta Quest 1 com Unity 2018.1.6f1**.
  Pela sua regra de descartar material anterior a Quest 2 / Unity 2021, **descartei estes
  números** — estão aqui só para você saber que existem e que estão velhos.

**NÃO VERIFICADO:** não encontrei nenhuma fonte citável com custo em milissegundos de dissolve,
alpha clip ou revelação progressiva em Quest 2/3. A única afirmação honesta que achei é que
o custo real só se conhece medindo no headset.

---

## FASE 3 — CORTE DE PELE — NÃO INICIADA

Bloqueada pelo portão que você definiu na Fase 1: não existe malha de tórax aberto (1.1).

Não levantei as três abordagens do Passo A como exercício de implementação, porque as três
dependem do mesmo asset inexistente, e o critério nº 2 que você mesmo definiu ("funciona com os
assets que realmente existem segundo 1.1") elimina todas antes da comparação. O que tenho de útil
sobre o assunto está em 2.4, e serve para quando a malha existir.

**Para destravar, você precisa decidir o que encomendar/produzir.** O mínimo que a mecânica que
você descreveu exige:

1. Uma segunda malha do tronco com a incisão mediana já esculpida e aberta, com as bordas de pele
   afastadas e faces internas modeladas (subcutâneo/músculo), alinhada ao mesmo transform da
   malha fechada.
2. Que ela seja submesh separado ou material separado, para a máscara poder revelar por região.

Sem isso, qualquer coisa que eu fizesse seria primitiva improvisada — que você proibiu, e com
razão: ficaria pior que nada diante de um avaliador.

---

## FASE 4 — QUALIDADE

### 4.1 Texto em inglês visível ao jogador — PRONTO

Varri `Assets/Scripts` separando tooltip de editor (não visível) de texto que chega à tela.

**Encontrado: exatamente duas frases**, ambas em `Assets/Scripts/UI/SurgeryHUD.cs:60`, no monitor
da sala da cena `SurgeryMVP`:

| Antes | Depois |
|---|---|
| `PROCEDURE COMPLETE` | `PROCEDIMENTO CONCLUÍDO` |
| `STANDBY` | `AGUARDANDO` |

Traduzidas. Nenhum teste dependia dessas strings (verificado antes de mexer).

**Não centralizei**, e explico por quê: você mandou centralizar "se estiver hardcoded em vários
lugares". As duas frases literais aparecem **uma vez cada**. O que se repete é o *conceito* —
"aguardando" aparece em `SurgeryHUD` (2×) e em `TransplantHUD` (1×), "procedimento concluído" em
`SurgeryHUD` (2×). Centralizar isso seria um refactor atravessando três HUDs, contra a regra de
mudança mínima e sem refactor amplo. Fica registrado como dívida, não executado.

**Já estava tudo em pt-BR** (nada a fazer): `ProjectionHUD`, `PortProjectionHUD`,
`SurgicalFeedbackHUD`, `TransplantHUD`, e todos os assets de dados em `Assets/Data/`.

**Inglês que NÃO é visível ao jogador, deixado como está** — vale você saber que existe:
`SurgeryTelemetry` registra em inglês ("Incision started", "Bleeding controlled", "Objective
completed", "Surgery completed") e `PatientController` levanta erros com texto em inglês
("Patient stability critical."). Rastreei os consumidores do `SurgeryEvents.OnError`: são
`SurgeryAudio`, `SurgeryEvaluation`, `SurgeryTelemetry` e `HapticManager` — **nenhum exibe o
texto**. É log de operador. Se um dia for para a projeção, precisa de tradução.

### 4.2 Teste de ergonomia — PRONTO, E ESTÁ VERMELHO POR ACHAR UM DEFEITO REAL

**Primeiro, uma correção à sua premissa: eu não deletei o `Tools_AreDistributedToBothSides`.**
Ver a seção de contradições. Ele já havia sido substituído antes desta sessão, por
`EveryTool_IsReachableWithEitherHand` em `WorkspaceErgonomicsTests.cs:118`, que mede o requisito
certo (os dois ombros) em vez da proxy errada (um instrumento de cada lado do eixo).

A lacuna real era outra, e essa eu preenchi: aquele teste roda **só na `SurgeryMVP`** e cobre
**só quem tem `SurgicalInteractable`**. A cena em desenvolvimento não era coberta, e o transplante
é feito majoritariamente em **pontos de interação nus** — 4 sítios de CEC, 5 anastomoses e a linha
média do esterno — que nenhum componente de instrumento toca.

Novo arquivo: `Assets/Tests/PlayMode/TransplantErgonomicsTests.cs`, dois testes sobre a
`TransplanteCardiaco`, cobrindo instrumentos **e** pontos de interação:

- `EveryInteractionPoint_IsReachableWithEitherHand` — **PASSA**
- `NothingEssential_SitsBeyondNinetyDegreesOfGaze` — **FALHA**

Medições (olho em `(0.45, 1.36, 0.47)`, olhando para −X):

| Ponto | Ombro esq. | Ombro dir. | Fora do eixo |
|---|---:|---:|---:|
| `Bypass_Wean` | 0,449 [Precision] | 0,497 [Precision] | 9° |
| `Vessel_InferiorVenaCava` | 0,485 [Precision] | 0,519 [Precision] | 6° |
| `Vessel_SuperiorVenaCava` | 0,517 [Precision] | 0,479 [Precision] | 7° |
| `SternalMidline` | 0,535 [Precision] | 0,524 [Precision] | 2° |
| `Bypass_Cardioplegia` | 0,550 [Precision] | 0,516 [Precision] | 6° |
| `Vessel_Aorta` | 0,556 [Comfortable] | 0,517 [Precision] | 7° |
| `Vessel_LeftAtrium` | 0,556 [Comfortable] | 0,553 [Comfortable] | 1° |
| `Bypass_Unclamp` | 0,560 [Comfortable] | 0,517 [Precision] | 7° |
| `Bypass_DeAir` | 0,565 [Comfortable] | 0,541 [Precision] | 4° |
| `Vessel_PulmonaryArtery` | 0,571 [Comfortable] | 0,544 [Precision] | 5° |
| `Heart` (nativo) | 0,542 [Precision] | 0,556 [Comfortable] | 2° |
| **`DonorHeart`** | 0,195 [Precision] | 0,551 [Comfortable] | **98°** ← |

**O defeito:** o coração do doador está a **98° do eixo do olhar**, além do limite de 90° que o
projeto já impõe na outra cena pelo mesmo motivo ergonômico. Alcance está bom; o problema é que
o visitante precisa girar o tronco para encontrá-lo — e o público é leigo de primeira viagem,
justamente quem não procura o que não está à vista.

**Não corrigi**, conforme a regra. A correção é uma linha em `BuildDonorHeart`
(`TransplanteSceneBuilder.cs`): o suporte está em `thorax + (x +0.50, z −0.35)`, atrás da linha
dos ombros. Trazer o `z` para perto de `thorax.z` (por exemplo `−0.05`) o põe ao lado do campo,
dentro do cone de visão. **Mas isso é encenação da cirurgia** — na sala real a bacia do doador
fica numa mesa lateral, e virar para pegá-la é fiel. Você decide entre fidelidade e achabilidade.

### 4.3 Suíte completa — 123 testes, 122 passando, 1 falhando

Número real, rodado. A única falha é o `NothingEssential_SitsBeyondNinetyDegreesOfGaze`, pelo
motivo acima. Deixei vermelho de propósito: o teste codifica um requisito seu e encontrou uma
violação verdadeira — deixá-lo verde exigiria afrouxar o limite, que seria esconder o problema.

---

## ONDE A PESQUISA CONTRADISSE ALGO AFIRMADO

Você pediu explicitamente. Três itens.

1. **"O `Tools_AreDistributedToBothSides` que você deletou"** — eu não deletei. Ele não existe
   mais, mas foi substituído antes desta sessão por `EveryTool_IsReachableWithEitherHand`, e o
   comentário em `WorkspaceErgonomicsTests.cs:118` documenta a troca e o raciocínio (a varredura
   de 107 layouts que mostrou o conflito com a regra dos 90°). Não houve deleção minha em
   nenhuma sessão.

2. **O README afirma "182 mil triângulos para a anatomia toda".** Medido: a anatomia na cena soma
   **242.000** triângulos (gradil 80.000 + coração nativo 60.000 + coração doador 60.000 +
   pele 29.999 + esterno 12.000). A cena inteira dá 296.408 estáticos e 338.948 em play mode.
   O número do README está **33% abaixo** do real. Não corrigi o README — é afirmação sua.

3. **A minha própria nota de sessão anterior dizia "a suíte fecha em 33/4, essas 4 são herdadas".**
   Falso hoje: a suíte fechou 121/121 no início desta sessão. Já corrigi essa anotação.

Além disso, uma ressalva à premissa do pedido original de Fase 3: você descreveu o dissolve
mascarado como a abordagem de referência. A recomendação de mobile da Meta é **evitar alpha-test
/ pixel discard**, que é o que um dissolve mascarado faz. Não é impeditivo — é um custo a medir
no headset antes de assumir que cabe. Marcado como não verificado por falta de número em ms.

---

## O QUE EU NÃO CONSEGUI VERIFICAR

1. **Custo em ms de dissolve/alpha-clip em Quest 2 ou 3.** Nenhuma fonte citável. Só a afirmação
   genérica de que alpha-test é caro em GPU de tile.
2. **Números de ergonomia da Meta** — alcance confortável em metros, altura de superfície de
   trabalho, limite angular. Duas páginas retornaram 404, a terceira não traz números. Por
   consequência, **as constantes do nosso `ReachEnvelope` (0,55 / 0,70 / 0,82 m e 90°) não têm
   fonte** — são estimativas internas que estamos tratando como requisito em teste.
3. **Draw calls reais no headset.** Medi no Editor, visão única, janela 815×420. Não sei o número
   em estéreo no dispositivo, e **não verifiquei o modo de stereo rendering do projeto** (se não
   for single-pass instanced, o custo de CPU pode quase dobrar).
4. **Frame time real.** Não medi ms/frame em lugar nenhum — nem no Editor nem no headset. Todo o
   raciocínio de orçamento acima é sobre contagens, não sobre tempo.
5. **Se a cena ainda é jogável de ponta a ponta com mãos reais.** Os testes provam a lógica e o
   layout; com o `XRHandInteractor` ausente (item 2 do topo), eu suspeito fortemente que **não
   é** — mas não consigo provar sem headset.
6. **Se `PATIENT_ExternalBody.fbx` e `PATIENT_Vertebrae.fbx` são usados.** Estão no projeto, não
   aparecem na cena do transplante. Não investiguei se alguma cena legada os usa.
7. **O impacto visual real da correção de decúbito no Quest.** Validei por render no Editor, com
   iluminação do Editor. Sombras e materiais em dispositivo não foram vistos.

---

## PROBLEMAS QUE A PESQUISA REVELOU — VOCÊ DECIDE O QUE ENTRA

Nenhum destes foi corrigido.

| # | Problema | Fonte | Custo estimado |
|---|---|---|---|
| 1 | `XRHandInteractor` ausente da cena; `IsHeld` sempre falso; coração nativo nunca conta como explantado | 2.3 + inspeção da cena | ~15 min, depois de escolher o interactor |
| 2 | `DonorHeart` a 98° do eixo do olhar | 4.2 (teste vermelho) | ~5 min (uma linha) + decisão de encenação |
| 3 | Malha da mesa com 52,5% das arestas abertas; produz fendas visíveis | 1.3 | Retopologia — decisão de arte |
| 4 | 98,5% da geometria é dinâmica; só 7 renderers marcados static | 1.4 + 2.1 | ~30 min marcar static o que não se move |
| 5 | 45 objetos lançando sombra com direcional *Soft* | 1.4 | ~15 min desligar sombra do que não precisa |
| 6 | Anatomia em malha cheia (gradil 80k, corações 60k cada) sem LOD | 1.1 + 2.1 | Só vale se 4 e 5 não resolverem — há folga de triângulos |
| 7 | README afirma 182k triângulos; real é 242k na anatomia | 2.1 | ~2 min |
| 8 | Constantes de `ReachEnvelope` sem fonte, usadas como requisito em teste | 2.2 | Decisão: validar no headset ou assumir |
| 9 | Não há repositório git; nenhum checkpoint desta sessão | Fase 1 | Decisão sua sobre onde vive o repo |
| 10 | Sem alvo visível no esterno: o jogador não sabe onde encostar a mão | pedido original, item 2 | ~15 min (anel visível como o das anastomoses) |

Sobre o **#10**: você pediu isso no primeiro rascunho da mensagem, e ele não reaparece no
briefing final — onde foi absorvido pela Fase 3, que está bloqueada. Como o gesto de esternotomia
**já está ativo e jogável** (foi ligado na sessão anterior) e continua sem marcador visível,
registro aqui em vez de implementar, seguindo a regra de escopo. É barato e eu recomendo fazer.

---

## ARQUIVOS TOCADOS NESTA SESSÃO

Sem commit — não há repositório.

| Arquivo | O quê |
|---|---|
| `Assets/Editor/TransplanteSceneBuilder.cs` | `BuildPatient`: rotação corrigida para decúbito dorsal |
| `Assets/Scripts/UI/SurgeryHUD.cs` | Duas frases traduzidas para pt-BR |
| `Assets/Tests/PlayMode/TransplantErgonomicsTests.cs` | **Novo.** Dois testes de ergonomia da cena do transplante |
| `Assets/Scenes/TransplanteCardiaco.unity` | Regerada pelo construtor (paciente supino) |
| `RELATORIO-SESSAO-NOTURNA.md` | Este arquivo |

---

## O QUE EU FARIA DIFERENTE COM MAIS TEMPO

- **Mediria ms/frame, não contagens.** Draw calls e triângulos são proxy; o requisito real é
  13,9 ms. Passei a noite com proxies porque não tenho headset, e isso limita todo o raciocínio
  de performance deste relatório.
- **Provaria o item 2 do topo com um teste**, em vez de deduzir da inspeção. Um teste que
  instancia o rig, agarra o coração e confirma que `RemoveNativeHeart` completa transformaria
  uma suspeita forte em fato — e teria pego isso antes de eu ligar a cena na sessão passada.
- **Teria começado pelo 1.1.** O portão decidiu a noite inteira em dez minutos de investigação;
  tudo que veio depois foi mais barato por já saber que o corte estava bloqueado.
