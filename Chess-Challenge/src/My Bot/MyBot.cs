using ChessChallenge.API;
using System;
using System.Linq;

// <summary>
// Chess bot "Thunker Weed" for SebLague Chess Challenge (https://github.com/SebLague/Chess-Challenge/tree/main/Chess-Challenge).
// Author: American Jeff
// 2023-09-11
// </summary>
public class MyBot : IChessBot
{
    struct Entry
    {
        public ulong zobrist;
        public int score, depth;
        public ushort move, cutoff;
    }
    readonly Entry[] tt = new Entry[0x800000];

    int timeLimit;
#if DEBUG
    int nodes;
#endif
    Board board;
    Timer timer;
    Move bestMove;
    readonly int[] killer = new int[1300];
    int[,,] history;

    public Move Think(Board botBoard, Timer moveTimer)
    {
        board = botBoard;
        timer = moveTimer;
        // Carve off 2/3 of the starting time allocation and determine time
        // limit from that.  Leads to more even pacing.
        timeLimit = Math.Min(timer.GameStartTimeMilliseconds * 2 / 3, timer.MillisecondsRemaining) / 14;
        history = new int[2, 64, 7];
#if DEBUG
        nodes = 0;
#endif
        int depthLimit = 2, alpha = -66666, beta = 66666, score = 0;
        while (depthLimit < 66 && score != 55555 /* TIMEOUT */)
        {
            score =
                NegaScout(alpha, beta, 0, depthLimit, true);
#if DEBUG
            if (depthLimit == 2) Console.WriteLine();
            string moveString = bestMove.ToString();
            string moveNumStr = String.Format("{0:N1}", board.PlyCount / 2f + 1);
            if (depthLimit == 2)
                Console.WriteLine("{0,4} {1,5} {2,7} {3,4}/{4,4} {5,6} {6,6}/s {7,7} {8,8}",
                    "#", "depth", "score", "used", "rem", "nodes", "nodes", "best", "The Thunker Weed");
            Console.WriteLine(
                "{0,4} {1,5} {2,7} {3,4}/{4,4} {5,6} {6,6}/s {7,7}",
                moveNumStr,
                depthLimit,
                score == 55555 ? "" : score,
                timer.MillisecondsElapsedThisTurn,
                score == 55555 ? "TIME" : timeLimit,
                nodes,
                1000 * nodes / (timer.MillisecondsElapsedThisTurn + 1),
                moveString);
#endif
            if (alpha < score && score < beta)
            {
                alpha = score - 10;
                beta = score + 10;
                depthLimit++;
            }
            else
            {
                beta = 66666;
                alpha = -66666;
            }
            // Early timeout.  0.34 factor determined by black box testing
            // with timeLimit = time/14.  Other winning combos were
            // 0.33 with time/13 and 0.35 with time/15.
            if (timer.MillisecondsElapsedThisTurn > 0.34f * timeLimit)
                break;
        }
        return bestMove;
    }

    int NegaScout(int alpha, int beta, int depth, int depthRemaining, bool nullMoveOk)
    {
    #if DEBUG
        nodes++;
    #endif
        ulong zobrist = board.ZobristKey, zindex = zobrist & 0x7FFFFF;
        var entry = tt[zindex];
        bool topLevel = depth == 0, isInCheck = board.IsInCheck();
        if (isInCheck)
            depthRemaining++;
        int score,
            bestScore = -666_666,
            moveNum = 0,
            entryScore = entry.score,
            entryCutoff = entry.cutoff;
        int Recurse(int bayta, int depthDecrement = 1, bool isNullMoveOk = true)
            => score = -NegaScout(-bayta, -alpha, depth + 1, depthRemaining - depthDecrement, isNullMoveOk);

        if (!topLevel && board.IsRepeatedPosition())
            return 0;

        // Check transposition table
        if (!topLevel && entry.zobrist == zobrist && entry.depth >= depthRemaining &&
            Math.Abs(entryScore) < 30_000 &&
            (
                entryCutoff == 2 /* EXACT */ ||
                entryCutoff == 0 /* BETA */ && entryScore >= beta ||
                entryCutoff == 1 /* ALPHA */ && entryScore <= alpha
            ))
            return entryScore;

        bool quiescence = depthRemaining <= 0;
        if (quiescence)
        {
            bestScore = StaticEvaluation();
            if (bestScore > alpha)
                alpha = bestScore;
            if (alpha >= beta)
                return alpha;
        }
        else if (!isInCheck && beta == alpha + 1)
        {
            if (depth > 2)
                // Extrapolate the quiescent search score of current node
                // proportional to depthRemaining and some magic constants.
                // If the extrapolations are outside alpha/beta then we prune.
                // Magic constants determined via black box tuning.
            {
                score = NegaScout(alpha, beta, depth, 0, true);
                if (score - 68 * depthRemaining >= beta || score + 87 * depthRemaining <= alpha)
                    return score;
            }
                //if ((score = NegaScout(alpha, beta, depth, 0, true)) - 68 * depthRemaining >= beta ||
                //    score + 87 * depthRemaining <= alpha)
                //    return score;
            if (nullMoveOk && depthRemaining > 1)
            {
                board.ForceSkipTurn();
                Recurse(beta, 3 + depthRemaining / 4 /* null move reduction value from Tyrant */, false);
                board.UndoSkipTurn();

                if (score >= beta)
                    return score;
            }
        }
        entry.cutoff = 1; // ALPHA
        Span<Move> moves = stackalloc Move[218];
        board.GetLegalMovesNonAlloc(ref moves, quiescence && !isInCheck);
        foreach (Move move in moves)
            killer[1000 + moveNum++] =
                move.RawValue == entry.move ? -1_000_000_000
                : move.IsCapture ? -10_000_000 * (int)move.CapturePieceType + (int)move.MovePieceType
                : killer[depth] == move.RawValue ? -1_000_000
                : history[depth % 2, move.TargetSquare.Index, (int)move.MovePieceType]
                ;
        killer.AsSpan(1000, moves.Length).Sort(moves);

        moveNum = 0;
        foreach (var move in moves)
        {
            board.MakeMove(move);

            if (moveNum++ == 0 || quiescence)
                Recurse(beta);
            else
            {
                int alphaPlus1 = score = alpha + 1;
                if (moveNum > 5 && depthRemaining > 1)
                    // short scout LMR
                    Recurse(alphaPlus1, 3);
                if (alpha < score)
                    // scout
                    Recurse(alphaPlus1);
                if (alpha < score && score < beta)
                    Recurse(beta);
            }
            board.UndoMove(move);
            if (depthRemaining > 2 && timer.MillisecondsElapsedThisTurn >= timeLimit)
                return 55555; //TIMEOUT
            if (score > bestScore)
                bestScore = score;
            if (score > alpha)
            {
                entry.cutoff = 2; //EXACT
                alpha = score;
                entry.move = move.RawValue;
                if (topLevel) bestMove = move;
            }

            if (alpha >= beta)
            {
                entry.cutoff = 0; //BETA
                if (!move.IsCapture && move.PromotionPieceType != PieceType.Queen)
                {
                    killer[depth] = move.RawValue;
                    history[depth % 2, move.TargetSquare.Index, (int)move.MovePieceType] -= depthRemaining * depthRemaining;
                }
                break;
            }
        }
        if (bestScore == -666_666)
            return isInCheck ? depth - 33333 : 0;

        entry.score = bestScore;
        entry.depth = depthRemaining;
        entry.zobrist = zobrist;
        tt[zindex] = entry;

        return bestScore;
    }

    int StaticEvaluation()
    {
        int egScore = 0,
            mgScore = 0,
            phaseWeight = 0,
            side = -1;

        while (++side < 2)
            for (int p = -1; ++p < 6;)
                for (ulong mask = board.GetPieceBitboard((PieceType)p + 1, side > 0); mask != 0;)
                {
                    int index = (BitboardHelper.ClearAndGetIndexOfLSB(ref mask) ^ 0b111000 * side) * 12 + p * 2, colorSign = 2 * side - 1;
                    phaseWeight += 0x42110 >> p * 4 & 0xf; // tyrant version. fewer tokens than (p + (p & 0b101)) / 2;
                    mgScore += ppt[index++] * colorSign;
                    egScore += ppt[index] * colorSign;
                }

        return (phaseWeight * mgScore + 24 * egScore - egScore * phaseWeight) / (board.IsWhiteToMove ? 24 : -24) + phaseWeight / 2;
    }

    // Read in range-compressed piece position tables and add piece values
    // (https://www.chessprogramming.org/PeSTO%27s_Evaluation_Function)
    static int bi;
    readonly int[] ppt = new[] {
            64011389261109431815273119744m,
            71819935927675683611806531584m,
            75527711785727240428128370688m,
            75811823298934744327225540608m,
            77325399015246207058715410432m,
            76415054076766120484005609472m,
            78300950002507342492240052224m,
            75525289413989155411896762368m,
            76777617637236014222454782274m,
            4022166558394757416218817883m,
            3078033696487222079489403689m,
            4017392743804176022631897920m,
            4017449208642650622976353326m,
            8352549487527753943897233749m,
            5229907893836313354943426583m,
            77347074621626337307500249081m,
            77365146269884723361665270012m,
            3733186321805683588946543621m,
            4952997509759107959633820178m,
            3391192854569898574978231573m,
            4625501670727735489924245036m,
            9598985063823378477802267686m,
            9302746181236771040552630289m,
            76734177620327003206625147378m,
            77975729200485644193926616823m,
            4934910421703457945967923209m,
            5251653838371907681492207876m,
            5858605434742890885439292174m,
            5856225546502405631273205520m,
            7097731212292275047190430472m,
            5869523748886934274706246668m,
            78889776640154751107045723376m,
            75785089723499793241670617582m,
            78608077684373462961528964863m,
            4620580485447512137361260285m,
            5229968804907575264525548296m,
            5842842378045647597824375564m,
            5225090692058955591292156676m,
            2139921537227797706920821255m,
            77329020312996645601386692591m,
            75504623618459930219892442094m,
            78908921113803679754131998205m,
            2457798026480182367984614653m,
            4604822058741166914660663801m,
            5225010411686604147318915074m,
            3689698306178672352656424194m,
            1844854041475946177151237910m,
            77658991040564475952666901496m,
            73659783651489136309177158120m,
            77068950090929389869006063103m,
            1233010017872865854483858930m,
            3044023286347775487464310768m,
            3060948339552827825654270454m,
            1225775425709592582340476944m,
            78308061332228553830301106458m,
            75521492096071442858069195761m,
            68385207668080906009952452608m,
            72140145210048004089950896128m,
            74906186485475475287419977728m,
            77327594324707689226520100864m,
            73355191271610006045598154752m,
            76730422305449627754850746368m,
            74296892318191835006387814400m,
            70265058374062339031171072000m
        }
            .SelectMany(decimal.GetBits)
            .Where((x, i) => i % 4 != 3)
            .SelectMany(BitConverter.GetBytes)
            .Select(b => new[]{
                // piece weights (mg, eg)
                82,  94,   // pawn
                337, 281,  // knight
                365, 297,  // bishop
                477, 512,  // rook
                1025, 936, // queen
                0,   0    // king
            }[bi++ % 12] + 1475 * (sbyte)b / 1000)
            .ToArray();
}
