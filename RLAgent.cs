using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Services
{
    public class RLAgent
    {
        private Dictionary<string, double[]> _qTable = new Dictionary<string, double[]>();
        private double _learningRate = 0.1;
        private double _discountFactor = 0.95;
        private double _epsilon = 0.1; // Exploration rate
        private Random _random = new Random();

        // Actions: 0=Hold, 1=Buy, 2=Sell
        private const int ActionCount = 3;

        public int GetAction(string state)
        {
            if (!_qTable.ContainsKey(state))
            {
                _qTable[state] = new double[ActionCount];
            }

            // Exploration
            if (_random.NextDouble() < _epsilon)
            {
                return _random.Next(ActionCount);
            }

            // Exploitation
            double[] qValues = _qTable[state];
            double maxQ = qValues.Max();
            int maxIndex = Array.IndexOf(qValues, maxQ);
            return maxIndex;
        }

        public void UpdateQValue(string state, int action, double reward, string nextState)
        {
            if (!_qTable.ContainsKey(state)) _qTable[state] = new double[ActionCount];
            if (!_qTable.ContainsKey(nextState)) _qTable[nextState] = new double[ActionCount];

            double currentQ = _qTable[state][action];
            double maxNextQ = _qTable[nextState].Max();

            // Q-Learning Formula
            double newQ = currentQ + _learningRate * (reward + _discountFactor * maxNextQ - currentQ);
            _qTable[state][action] = newQ;
        }

        public string GetStateKey(double rsi, double macd, double bbPosition)
        {
            // Discretize continuous values into buckets for Q-Table
            return $"{Math.Round(rsi / 10)}_{Math.Round(macd, 2)}_{Math.Round(bbPosition, 1)}";
        }
    }
}