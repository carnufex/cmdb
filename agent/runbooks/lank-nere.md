# Runbook: länk nere på en station (syntetisk)

Gäller när en uppringare rapporterar "ingen länk", "länken är nere" eller "tappat trafik" på en station.

1. Bekräfta stationen och verifiera uppringaren.
2. Fråga om det gäller en enskild port eller hela stationen, och sedan när felet började.
3. Om uppringaren står på plats:
   - Kontrollera att utrustningen har ström. Titta på lysdioderna på strömförsörjningen och likriktaren.
   - Kontrollera länklampan på porten. Om den är släckt: kontrollera att patchkabeln sitter i båda ändar, och byt patchkabel om en reserv finns.
   - Titta i ODF:en efter synliga skador eller böjda fibrer.
4. Kör påverkansanalysen för stationen. Om kritiska tjänster saknar fungerande väg är det P1, och jouren larmas när ärendet skapas.
5. Skapa ärendet med det uppringaren sett och gjort.
6. Gör inga omkopplingar på plats utan NOC:s klartecken, även om en ledig port finns. En ändring görs som plan i CMDB:n och godkänns av en människa.
