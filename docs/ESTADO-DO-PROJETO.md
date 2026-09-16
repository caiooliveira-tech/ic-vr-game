# Simulador de Transplante Cardíaco em VR — Estado real do projeto

**Iniciação Científica · FIAP**  
**Data deste levantamento:** 13 de setembro de 2026  
**Destino:** estande do evento Next

---

## Aviso sobre este documento

Este é um levantamento honesto, feito medindo o projeto e não estimando. Onde um número foi
medido, ele está aqui com o valor real. Onde não foi possível verificar, está escrito
"não verificado" em vez de uma suposição.

A parte mais importante deste documento não é a lista do que funciona — é a seção
**"O que ainda não funciona"** e a seção **"O que nunca foi testado"**. O projeto tem uma base
sólida em um aspecto específico e fragilidades reais em outros, e misturar os dois numa
apresentação otimista seria inútil para decidir o que fazer no tempo que resta.

---

## 1. O que o projeto é

Uma experiência de realidade virtual em que um visitante leigo executa um transplante de
coração, do início ao fim, em poucos minutos. Pensada para estande de evento com fila: a pessoa
coloca o óculos, opera, o resultado vai para um placar, e a próxima pessoa entra.

**Plataforma:** Meta Quest rodando sozinho (APK Android, sem PC).
**Motor:** Unity 6000.3.22f1, URP, OpenXR, XR Interaction Toolkit 3.5.1.
**Locomoção:** nenhuma. O visitante fica parado ao lado da mesa cirúrgica.

### A cirurgia modelada

```
Abrir o tórax
   → Entrar em circulação extracorpórea (canular · clampear · cardioplegia)
      → Retirar o coração doente
         → Posicionar o coração do doador
            → Conectar 5 vasos (átrio esquerdo, 2 cavas, aorta, artéria pulmonar)
               → Sair de bomba (desclampear · desarejar · desmamar)
                  → O coração volta a bater
```

A ordem é **imposta**, não sugerida. E cada recusa explica a consequência clínica — é esse o
diferencial didático do projeto, e é a parte mais bem resolvida dele.

---

## 2. O que efetivamente funciona hoje

### 2.1 A lógica clínica — esta é a parte forte

A regra clínica está implementada em C# puro, **sem nenhuma dependência do Unity**, o que
permite testá-la inteira sem cena, sem óculos e sem renderização. Isso não é detalhe de
engenharia: significa que a correção clínica é verificável automaticamente e não depende de
alguém jogar para descobrir que quebrou.

**Cada recusa carrega o motivo clínico.** Exemplos reais, como aparecem na tela:

| Erro do visitante | O que o sistema responde |
|---|---|
| Cardioplegia antes de clampear | "A cardioplegia só protege com a aorta clampeada — sem o clampe ela é lavada pela circulação e o coração não para." |
| Clampear sem estar em bomba | "Clampear a aorta sem estar em bomba interrompe a circulação do paciente." |
| Desclampear com anastomoses abertas | "Desclampar antes de terminar as anastomoses esvazia o paciente no tórax — os vasos ainda estão abertos." |
| Sair de bomba sem desarejar | "Sair de bomba com ar nas câmaras manda êmbolo gasoso para o cérebro." |

Há três níveis de dificuldade que mudam **quanto** da circulação extracorpórea o visitante
executa, e não as regras. Um nível mais fácil é um visitante que chega mais tarde na cirurgia —
nunca um paciente para quem as regras deixaram de valer. O preparo feito "pela equipe" antes da
chegada do visitante passa pelas mesmas validações, de modo que nenhum nível consegue produzir
um paciente que o procedimento não poderia ter produzido.

### 2.2 Volume de código

| Camada | Tamanho |
|---|---|
| Scripts de runtime | 69 arquivos, 9.373 linhas |
| Scripts de editor (geração de cena, diagnóstico) | 5.082 linhas |
| Testes automatizados | 3.612 linhas, **127 métodos de teste** |

As cenas são **geradas por código**, não montadas à mão. Isso permite reconstruir o cenário
inteiro de forma reproduzível e evita que ajustes manuais se percam.

### 2.3 Corrigido nesta última fase de trabalho

- **O paciente estava deitado de bruços.** Corrigido para decúbito dorsal, com verificação por
  medida da malha (a orientação frontal do modelo foi determinada medindo a projeção dos pés e
  dos glúteos, não no olho) e confirmação visual de frente e de perfil.
- **A cena não iniciava o procedimento.** A lógica clínica existia completa e testada, mas nada
  na cena a acionava: o cronômetro nunca partia, concluir a cirurgia não vencia a rodada, e o
  paciente não voltava ao estado inicial entre visitantes. Ligado e coberto por testes.
- **As mãos não eram reconhecidas como "segurando".** Faltava na cena o componente que traduz os
  eventos de agarre do XR Interaction Toolkit para o contrato do projeto. Consequência: o coração
  doente nunca era registrado como retirado, e o coração do doador contava como implantado ainda
  na mão do visitante. **A correção foi escrita e está na cena; ainda não foi comprovada por
  execução de teste** (ver seção 5).
- **Não havia marcação visível do ponto de incisão** no esterno. Adicionado.

---

## 3. O que ainda NÃO funciona

Esta é a seção que importa para decidir prioridade.

### 3.1 Não existe malha de tórax aberto — este é o bloqueio central

Foi feito um inventário literal de todos os modelos anatômicos do projeto. **Nenhum deles
representa um tórax aberto**: não há bordas de pele afastadas, não há subcutâneo, não há músculo,
não há cavidade. Todas as malhas são casca única fechada, um objeto, um material.

Consequência prática, e ela é séria:

> Quando o visitante "abre o tórax", o esterno se afasta — mas **a pele do paciente continua
> fechada e opaca**. O coração é ativado dentro de um corpo intacto. O visitante não vê nada
> mudar, e não vê o órgão que precisa retirar.

Medido: a superfície da pele sobre o esterno está a 1,233 m de altura; o coração está a ~1,12 m.
Ele fica embaixo. A pele não tem colisor, então a mão atravessa e a mecânica funciona — mas
visualmente é impossível perceber isso.

**Isto não tem solução em código.** É preciso produzir ou adquirir a malha de tórax aberto. É
decisão de arte/modelagem, e é o item que mais trava o projeto.

Pelo mesmo motivo, os anéis que marcam os cinco pontos de anastomose também ficam atrás da pele
opaca, invisíveis.

### 3.2 Seis pontos onde o visitante fica preso

Levantados percorrendo o procedimento etapa por etapa:

| # | Problema | Natureza | Custo estimado |
|---|---|---|---|
| 1 | A instrução diz "abra o tórax com a serra esternal" — **não existe serra na cena**; o gesto é com a mão | Texto | 2 min |
| 2 | Nenhum gesto por tempo mostra progresso. O visitante segura a mão parada por 3 s sem retorno visual e conclui que não funcionou | Interface | ~30 min |
| 3 | Os 4 pontos de circulação extracorpórea são **invisíveis** (raio de 2,8 cm, sem marcador) | Código | ~15 min |
| 4 | A pele fechada esconde o coração (item 3.1) | **Precisa de modelo 3D** | arte |
| 5 | Os anéis das anastomoses ficam atrás da pele (item 3.1) | **Precisa de modelo 3D** | arte |
| 6 | O coração do doador está a 98° do eixo do olhar — fora do limite ergonômico de 90° do próprio projeto; o visitante precisa girar o tronco para encontrá-lo | Layout | 5 min + decisão |

**Quatro dos seis são sinalização, não lógica**, e somam cerca de uma hora de trabalho sem
depender de nenhum modelo novo. Nenhuma etapa deixa de completar por erro de regra: o que trava
é o visitante não saber o que fazer, não o sistema recusar o que ele fez.

### 3.3 Qualidade dos modelos anatômicos

Os modelos foram gerados por ferramenta de IA e **não passaram por validação anatômica**.
Problemas medidos:

- **Gradil costal proporcionalmente estreito:** 23,8 × 16,0 × 30,0 cm, contra cerca de
  28 × 20 × 30 cm de um tórax adulto real. É escala uniforme, sem distorção — mas é um tórax
  magro.
- **Malhas não são sólidos fechados.** A mesa cirúrgica, por exemplo, tem 26.664 arestas de borda
  abertas em 50.832 — mais de metade. São retalhos desconectados, o que produz fendas visíveis na
  geometria. O coração e o conjunto de vasos têm o mesmo problema.
- **Os vasos não são segmentáveis.** Não foi possível separar o modelo nos cinco vasos
  individuais por nenhum método tentado. Por isso os pontos de anastomose são marcadores
  posicionados manualmente (anéis coloridos), e não a boca real de cada vaso.

**Para o orientador:** este é o ponto onde eu gostaria de orientação específica. As proporções e
as posições anatômicas atuais foram derivadas geometricamente e conferidas por imagem, mas nunca
por alguém da área. Se a precisão anatômica é requisito de apresentação, os modelos precisam de
revisão clínica — e possivelmente de substituição.

### 3.4 Ausências de infraestrutura

- **Não há controle de versão.** A pasta é um ZIP extraído, sem repositório Git. Nenhum histórico,
  nenhum ponto de retorno. É um risco concreto de perda de trabalho a menos de um mês do evento.
- **Sem áudio e sem retorno háptico** na cena do transplante. Existem sistemas implementados para
  ambos, usados nas cenas antigas, mas não ligados à cena atual.
- **Sem projeção para a plateia.** O sistema existe, mas depende de uma segunda saída de vídeo
  (Display 2) que **não existe em Quest rodando sozinho**. Num estande, a fila não vê o que o
  jogador está fazendo.
- **Sem entrada de nome no placar.** Todas as partidas entram como "Anônimo".

---

## 4. Desempenho

Medido no editor, com uma única visão (não estéreo):

| Métrica | Valor medido | Referência da Meta para Quest | Situação |
|---|---:|---|---|
| Draw calls | 107 | 80–200 (Quest 2) · 200–300 (Quest 3) | dentro |
| Triângulos | 338.948 | 750 mil – 1 milhão (Quest 2) | ~45% do teto |
| Taxa de quadros alvo | 72 FPS = 13,9 ms/quadro | mínimo 72 FPS | — |

Depois de marcar como estático o que não se move e desligar sombra do que não é visível:

| Métrica | Antes | Depois |
|---|---:|---:|
| Triângulos dinâmicos | 292.024 | **156.843** (−46%) |
| Objetos lançando sombra | 45 | 42 |

**Conclusão:** contagem de triângulos não é o gargalo — há folga confortável. O documento anterior
do projeto afirmava 182 mil triângulos para a anatomia; a medição real deu **242 mil**, 33% acima.
Esse número foi corrigido.

**Ressalva importante:** tudo isto foi medido no editor em Linux, numa janela pequena e sem
renderização estéreo. **Não é medição no óculos.** O custo real em milissegundos por quadro
nunca foi medido em lugar nenhum — nem no editor, nem no dispositivo.

---

## 5. O que nunca foi testado

Esta lista não é confortável, mas é a parte mais importante do documento.

1. **Nada foi jogado com o óculos. Nenhuma vez.** Todo o desenvolvimento foi feito em Linux, sem
   Quest Link. Os tempos de gesto, os raios de tolerância de 2,2 cm e a legibilidade do monitor a
   30 cm do olho são estimativas informadas, não medições.
2. **O ciclo completo com as mãos nunca foi executado.** A correção do reconhecimento de agarre
   (seção 2.3) foi escrita, compilou sem erros e está na cena, mas o teste que a comprova **nunca
   chegou a rodar** — o editor do Unity entrou em estado degradado ao final da sessão. Até que
   esse teste rode, a afirmação "o jogo fecha de ponta a ponta" **não está comprovada**.
3. **Estado real da suíte de testes:** existem 127 métodos de teste declarados. A última execução
   completa cobriu 123 deles: **122 passaram e 1 falhou**. Os 4 testes mais recentes — justamente
   os que comprovariam o item 2 acima — nunca foram executados.
   - A falha conhecida é o teste de ergonomia, e ela é legítima: aponta o coração do doador a 98°
     do eixo do olhar (item 6 da seção 3.2). Foi deixada vermelha de propósito, porque afrouxar o
     limite esconderia o problema em vez de resolvê-lo.
4. **Custo de desempenho em estéreo no dispositivo.** Pode ser substancialmente maior que o
   medido, dependendo da configuração de renderização — que também não foi verificada.
5. **Conforto da experiência.** Os parâmetros de ergonomia que o projeto impõe como requisito
   (alcance confortável de 0,55 m a 0,82 m, limite de 90° do eixo do olhar) são estimativas
   internas. A documentação oficial da Meta, consultada em 13/09/2026, **não publica valores
   numéricos** de alcance ou de limite angular. Ou seja: estamos tratando como requisito números
   que não têm fonte.

---

## 6. Avaliação honesta e riscos para o evento

### O que está sólido

A modelagem do procedimento cirúrgico. A ordem é imposta, os erros clínicos são nomeados e
explicados, os três níveis não relaxam nenhuma regra, e tudo isso é verificado automaticamente.
Se o objetivo é **ensinar a sequência e o porquê de cada passo**, o núcleo do projeto cumpre.

### O que está frágil

A camada entre esse núcleo e o visitante. Hoje a pessoa tem uma cirurgia correta acontecendo
atrás de uma apresentação que não mostra onde tocar, não confirma que o gesto está funcionando,
e não abre o tórax visualmente. Um conteúdo correto que não é percebido não ensina.

### Riscos concretos, em ordem

1. **A malha de tórax aberto não chegar a tempo.** É o item de maior prazo e o único que não
   depende de mim. Sem ela, a cirurgia acontece dentro de um corpo fechado.
2. **Nunca ter validado no óculos.** Toda estimativa de conforto, alcance e legibilidade pode
   estar errada, e só se descobre vestindo. Esta é a primeira coisa a fazer.
3. **Ausência de controle de versão** a menos de um mês do evento.
4. **A fila não ver nada.** Sem projeção, o estande perde a função de atrair público.

### O que eu proponho como ordem de trabalho

1. Criar repositório Git (minutos).
2. Rodar a suíte completa e fechar a verificação pendente do item 5.2 (minutos).
3. Sessão com o óculos, percorrendo a cirurgia inteira — antes de qualquer trabalho novo.
4. Os quatro itens de sinalização da seção 3.2 (~1 h, sem depender de arte).
5. Decisão sobre a malha de tórax aberto — **é aqui que eu preciso da orientação**.

---

## 7. Perguntas ao orientador

1. **Precisão anatômica:** os modelos são gerados por IA e não foram validados clinicamente. O
   gradil costal está proporcionalmente estreito. Isso é aceitável para a proposta didática, ou
   precisa de revisão/substituição antes do evento?
2. **Tórax aberto:** existe alguma fonte de modelo anatômico de tórax aberto (pele afastada,
   subcutâneo, músculo) que a instituição possa disponibilizar? É o bloqueio de maior prazo.
3. **Escopo da simulação:** a cirurgia hoje tem 6 etapas com tolerância generosa de tempo e
   posição, pensada para leigo em 2 a 4 minutos. Essa simplificação preserva o valor didático que
   o senhor espera, ou há etapas cuja omissão compromete o ensino?
4. **Os textos clínicos das recusas** (exemplos na seção 2.1) foram redigidos por mim a partir de
   literatura geral. Peço revisão dessas frases — é o conteúdo que o visitante efetivamente lê.
