using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace Autonoceptor.Shared
{
    public class AngleDataPoints : SortedList<double, double>
    {

        private int _distanceDifferenceToDefineBoarder = 50;

        /// <summary>
        /// A function that finds the left boarder of the closest object. Returns null if that boarder was not scanned. 
        /// </summary>
        /// <returns></returns>
        public double? FindLeftOfClosestObject()
        {
            // Find what the closest value is:
            var minimumDistance = this.IndexOfValue(this.Values.Min());

            int closeIterator = minimumDistance;
            int farIterator = minimumDistance - 1; 
            while (farIterator >= 0)
            {
                if (this[farIterator] - this[closeIterator] > _distanceDifferenceToDefineBoarder)
                {
                    return this.ElementAt(farIterator).Key;
                }
                closeIterator = farIterator;
                farIterator = farIterator - 1;
            }
            return null;
        }

        /// <summary>
        /// A function that finds the right boarder of the closest object. Returns null if that boarder was not scanned. 
        /// </summary>
        /// <returns></returns>
        public double? FindRightOfClosestObject()
        {
            // Find what the closest value is:
            var minimumDistance = this.IndexOfValue(this.Values.Min());

            int closeIterator = minimumDistance;
            int farIterator = minimumDistance + 1;
            while (farIterator <= this.Count - 1)
            {
                if (this[farIterator] - this[closeIterator] > _distanceDifferenceToDefineBoarder)
                {
                    return this.ElementAt(farIterator).Key;
                }
                closeIterator = farIterator;
                farIterator = farIterator + 1;
            }
            return null;
        }

        /// <summary>
        /// Finds the largest gap between angles in the dataset. 
        /// </summary>
        /// <returns>[0] is the size of the largest gap, [1] is the average of the two keys on either side of the gap.</returns>
        public Tuple<double, double> FindLargestGap()
        {

            double largestGap = 0;
            double bisector = 0;

            for (int q = 1; q <= this.Count; q++)
            {
                if (this.Keys[q] - this.Keys[q-1] > largestGap)
                {
                    largestGap = this.Keys[q] - this.Keys[q - 1];
                }
            }

            this.Count
        }

    }
}
