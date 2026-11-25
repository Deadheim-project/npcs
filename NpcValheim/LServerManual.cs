namespace NpcValheim
{
    internal static partial class L
    {
        // Pares escritos a mao: mensagens montadas por concatenacao e palavras soltas
        // que o servidor encaixa dentro de outras frases.
        private static void LoadManual()
        {
            Add("moedas", "coins");
            Add("Loja do Pântano", "Swamp Shop");
            Add("Loja da Montanha", "Mountain Shop");
            Add("Loja da Planície", "Plains Shop");
            Add("Loja de Mistlands", "Mistlands Shop");
            Add("Loja de Ashlands", "Ashlands Shop");
            Add("Loja do Norte Profundo", "Deep North Shop");
            Add("Ancião", "Elder");
            Add("Rainha", "Queen");
            Add("Compra em (.+)", "Purchase at $1");
            Add("Recompensa: (.+)", "Reward: $1");
            Add("Desafio: (.+)", "Challenge: $1");
            Add("Arena balanceada: (.+)", "Balanced arena: $1");
            Add("Pacote de (.+)", "Package from $1");
            Add("Intendente da Arena", "Arena Steward");
            Add("Mestre da Arena", "Arena Master");
            Add("O FFA está pronto ((.+) jogadores)! Abra a Arena (Tab > Arena) e clique Entrar ((.+)s).", "The FFA is ready ($1 players)! Open the Arena (Tab > Arena) and click Enter ($2s).");
            Add("O FFA balanceado está pronto ((.+) jogadores)! Abra a Arena (Tab > Arena) e clique Entrar ((.+)s).", "The balanced FFA is ready ($1 players)! Open the Arena (Tab > Arena) and click Enter ($2s).");
            Add("(.+) balanceada", "$1 balanced");
            Add("Ranqueada", "Ranked");
            Add("Escaramuça", "Skirmish");
            Add("todos contra todos", "free for all");
            Add("Todos contra todos", "Free for all");
            Add("saiu", "left");
            Add("fugiu", "fled");
            Add("offline", "offline");
            Add("de pé", "standing");
            Add("derrotado", "defeated");
            Add("Grupo de (.+)", "$1's group");
            Add("Time (.+)", "Team $1");
            Add("vitória", "win");
            Add("participação", "participation");
            Add("(.+) moedas", "$1 coins");
            Add("Temporada: \"(.+)\" concluído! +(.+) pontos (nível (.+))", "Season: \"$1\" complete! +$2 points (level $3)");
            Add("Temporada: \"(.+)\" concluído! +(.+) pontos", "Season: \"$1\" complete! +$2 points");
            Add("O passe agora custa (.+) Deadcoins. Confira e pague de novo.", "The pass now costs $1 Deadcoins. Check it and pay again.");
            Add("Você não tem Deadcoins suficientes: saldo (.+), custa (.+)", "You do not have enough Deadcoins: balance $1, it costs $2");
            Add("Missão bloqueada. Requisitos: (.+)", "Quest locked. Requirements: $1");
            Add("Requer: (.+)", "Requires: $1");
            Add("Não deu para viajar: (.+)", "Could not travel: $1");
            Add("O passe de (.+) ainda não pode ser comprado com Deadcoins: ninguém derrotou (.+) neste servidor ainda. Derrote-o ou pague em moedas, se a loja aceitar.",
                "The $1 pass cannot be bought with Deadcoins yet: nobody has defeated $2 on this server yet. Defeat it or pay in coins, if the shop accepts it.");
            Add("Venda", "Sale");
            Add(" por (.+) un", " for $1 ea");
            Add("Proteção ativa: a próxima morte para monstro, queda ou afogamento tira (.+)% menos skill.",
                "Protection active: the next death to a monster, fall or drowning takes $1% less skill.");
            Add("Proteção ativa: a próxima morte tira (.+)% menos skill.",
                "Protection active: the next death takes $1% less skill.");
        }
    }
}
