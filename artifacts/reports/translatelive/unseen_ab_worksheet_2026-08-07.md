# Blinded en->tl Evaluation — Unseen Conversational Set (2026-08-07)

Engine: Universal Captions faster-whisper-native STT (small) -> candidate translator.
Sample: `artifacts/samples/english_unseen_90s_16k.wav` (16 scripted lines, 92.85s).
Candidates A and B are two different en->tl translation pipelines. One candidate is the
same in every row; do NOT try to guess which. Score each line independently.

Rate each candidate line: **Naturalness** (1=broken/stilted, 5=natural native speech)
and **Meaning** (1=wrong/dropped, 5=faithful to the English source).
Then pick your **preference**: A, B, or Tie.

---

**1.** EN: "Hi everyone, I'm Alex, and this is my friend Maya."
- A: `Ako si Alex, at ito ang kaibigan kong si Maya.`
- B: `Hi sa lahat. Ako si Alex, at ito ang kaibigan kong si Maya.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**2.** EN: "Welcome back to class. Did you all have a good weekend?"
- A: `Maligayang pagbabalik sa klase. Nagkaroon ba kayo ng magandang weekend?`
- B: `Maligayang pagdating sa klase. / Maganda ba ang dulo ng sanlinggo ninyo?`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**3.** EN: "Today let's talk about everyday plans and simple requests."
- A: `Ngayon pag-usapan natin ang mga pang-araw-araw na plano at simpleng kahilingan.`
- B: `Pag - usapan natin ngayon ang tungkol sa pang - araw - araw na mga plano at simpleng mga kahilingan.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**4.** EN: "First, who can tell me the time? It's almost nine thirty."
- A: `Una, sino ang makapagsasabi sa akin ng oras? / Halos 9.30.`
- B: `Una, sino ang makakapagsabi sa akin ng oras? Ito ay halos 9:30.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**5.** EN: "My birthday falls on the twenty first of December, and I just turned thirty."
- A: `Ang kaarawan ko ay sa ika-21 ng Disyembre. At nag-30 na ako.`
- B: `Ang aking kaarawan ay pumapatak sa ika - 21 ng Disyembre, at ako'y 30 na lamang.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**6.** EN: "Could you please pass me the notes from yesterday's meeting?"
- A: `Puwede mo bang ipasa sa akin ang mga nota mula sa pulong kahapon?`
- B: `Pwede mo bang ipasa ang mga notes mula sa meeting kahapon?`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**7.** EN: "Sure, here you go. Thanks a lot, I really appreciate it."
- A: `Sige. Heto. Maraming salamat. Talagang naa-appreciate ko.`
- B: `Totoo, narito ka. / Maraming salamat, talagang pinahahalagahan ko ito.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**8.** EN: "The Green Valley Cooking Club meets every Saturday at the community center."
- A: `Ang Green Valley Cooking Club ay nagpupulong tuwing Sabado sa Community Center.`
- B: `Ang Green Valley Cooking Club ay nagpupulong tuwing Sabado sa community center.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**9.** EN: "She said he would bring his guitar, but we never saw it arrive."
- A: `Siya ay nagsabing dadalhin niya ang kanyang gitara. Pero hindi namin nakita na dumating.`
- B: `Sinabi niya na dadalhin ng lalaki ang gitara nito, subalit hindi namin nakita ang pagdating nito.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**10.** EN: "I'm feeling a bit under the weather today, so I'll take it easy."
- A: `Medyo masama ang pakiramdam ko ngayon, kaya magpapahinga na lang ako.`
- B: `Medyo masama ang pakiramdam ko ngayon, kaya madali lang.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**11.** EN: "That joke you told earlier was hilarious. I couldn't stop laughing."
- A: `Nakagigitla ang birong sinabi mo noon. / Hindi ko mapigil ang pagtawa.`
- B: `Ang biro na sinabi mo kanina ay nakakatawa. Hindi ko mapigilan ang pagtawa.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**12.** EN: "We're running a little behind schedule, so please bear with us."
- A: `Medyo nahuhuli na tayo sa iskedyul. Kaya pasensya na muna kayo sa amin.`
- B: `Medyo nahuhuli na tayo, kaya pakisuyong pagtiisan mo kami.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**13.** EN: "Take care on your way home, and see you around soon."
- A: `Mag-ingat ka sa pag-uwi. At magkita tayo ulit sa lalong madaling panahon.`
- B: `Mag - ingat sa iyong pag - uwi, at makita ka agad.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**14.** EN: "Let's catch up over lunch sometime. It's been a while since we talked."
- A: `Paminsan - minsan, kumain tayo. / Mula nang kami'y mag - usap.`
- B: `Mag-usap tayo sa tanghalian minsan. Matagal na simula nung nag-usap tayo.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**15.** EN: "Could you speak up a little? The projector is a bit noisy today."
- A: `Puwede ka bang magsalita nang kaunti? / Ang projector ay medyo maingay ngayon.`
- B: `Pwede mo bang lakasan nang kaunti? Ang projector ay medyo maingay ngayon.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

**16.** EN: "Alright, that's all for today. Thanks for coming, and have a great week."
- A: `Sige, yun lang para sa araw na ito. Salamat sa pagpunta, at magandang linggo sa inyo.`
- B: `O sige, para lang sa ngayon. / Salamat sa inyong pagdating, at mayroon po kayong isang malaking linggo.`
- Naturalness: A (1-5) ___ B (1-5) ___ | Meaning: A ___ B ___ | Preferred: ___

---

Summary:
- Naturalness means (A / B): ___ / ___
- Meaning means (A / B): ___ / ___
- Preference totals: A ___, B ___, Tie ___
- Overall: which candidate would you rather see as live captions? ___
